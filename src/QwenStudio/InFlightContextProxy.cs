using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QwenStudio;

public sealed class InFlightContextProxy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstreamClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _targetBaseUrl;
    private readonly int _listenPort;
    private readonly AstService _astService = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public Func<bool>? IsPruningEnabled { get; set; }
    public Func<int>? GetPruningThresholdTokens { get; set; }
    public Action<int, int, int>? OnPruned { get; set; }

    public InFlightContextProxy(string targetBaseUrl = "http://127.0.0.1:8081", int listenPort = 8082)
    {
        _targetBaseUrl = targetBaseUrl.TrimEnd('/');
        _listenPort = listenPort;
    }

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener.Prefixes.Add($"http://127.0.0.1:{_listenPort}/");
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InFlightContextProxy failed to start: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error accepting proxy context: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var rawPath = ctx.Request.Url?.PathAndQuery ?? "/";
        var isChatCompletion = rawPath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);

        try
        {
            byte[]? forwardBodyBytes = null;

            if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && isChatCompletion)
            {
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms, ct);
                var rawBody = Encoding.UTF8.GetString(ms.ToArray());

                bool enabled = IsPruningEnabled?.Invoke() ?? true;
                int threshold = GetPruningThresholdTokens?.Invoke() ?? 40000;

                if (enabled)
                {
                    forwardBodyBytes = TryPrunePayload(rawBody, threshold);
                }
                else
                {
                    forwardBodyBytes = ms.ToArray();
                }
            }

            var targetUrl = $"{_targetBaseUrl}{rawPath}";
            using var forwardReq = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), targetUrl);

            if (ctx.Request.Headers.AllKeys != null)
            {
                foreach (string? headerKey in ctx.Request.Headers.AllKeys)
                {
                    if (string.IsNullOrEmpty(headerKey) ||
                        string.Equals(headerKey, "Host", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(headerKey, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var vals = ctx.Request.Headers.GetValues(headerKey);
                    if (vals != null)
                    {
                        forwardReq.Headers.TryAddWithoutValidation(headerKey, vals);
                    }
                }
            }

            if (forwardBodyBytes != null)
            {
                forwardReq.Content = new ByteArrayContent(forwardBodyBytes);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                {
                    forwardReq.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
                }
            }
            else if (ctx.Request.HasEntityBody)
            {
                forwardReq.Content = new StreamContent(ctx.Request.InputStream);
            }

            using var upstreamResp = await _upstreamClient.SendAsync(forwardReq, HttpCompletionOption.ResponseHeadersRead, ct);

            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            ctx.Response.StatusDescription = upstreamResp.ReasonPhrase ?? "OK";

            if (upstreamResp.Content.Headers.ContentType != null)
            {
                ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType.ToString();
            }

            if (upstreamResp.Headers.TransferEncodingChunked == true || upstreamResp.Headers.Contains("Transfer-Encoding"))
            {
                ctx.Response.SendChunked = true;
            }
            else if (upstreamResp.Content.Headers.ContentLength.HasValue)
            {
                ctx.Response.ContentLength64 = upstreamResp.Content.Headers.ContentLength.Value;
            }

            foreach (var header in upstreamResp.Headers)
            {
                if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    continue;

                try { ctx.Response.Headers[header.Key] = string.Join(", ", header.Value); } catch { }
            }
            foreach (var header in upstreamResp.Content.Headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

                try { ctx.Response.Headers[header.Key] = string.Join(", ", header.Value); } catch { }
            }

            using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(ctx.Response.OutputStream, 8192, ct);
            await ctx.Response.OutputStream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 502;
                var errBytes = Encoding.UTF8.GetBytes($"Proxy error forwarding to upstream: {ex.Message}");
                await ctx.Response.OutputStream.WriteAsync(errBytes, ct);
            }
            catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private byte[] TryPrunePayload(string rawJson, int thresholdTokens)
    {
        try
        {
            int estimatedTokens = rawJson.Length / 4;
            if (estimatedTokens < thresholdTokens)
            {
                return Encoding.UTF8.GetBytes(rawJson);
            }

            var root = JsonNode.Parse(rawJson);
            var messages = root?["messages"]?.AsArray();
            if (messages == null || messages.Count < 10)
            {
                return Encoding.UTF8.GetBytes(rawJson);
            }

            // Leave the last 8 messages (working memory) untouched!
            int cutoff = messages.Count - 8;
            bool prunedAny = false;

            // Build dictionaries of tool call ID -> label & file path
            var toolCallInfo = new Dictionary<string, string>();
            var toolPathInfo = new Dictionary<string, string>();
            foreach (var m in messages)
            {
                var toolCalls = m?["tool_calls"]?.AsArray();
                if (toolCalls != null)
                {
                    foreach (var tc in toolCalls)
                    {
                        var id = tc?["id"]?.GetValue<string>();
                        var fnName = tc?["function"]?["name"]?.GetValue<string>() ?? "tool";
                        var args = tc?["function"]?["arguments"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrEmpty(id))
                        {
                            string target = fnName;
                            if (args.Contains("\"path\":") || args.Contains("\"file_path\":"))
                            {
                                var match = Regex.Match(args, @"""(?:file_)?path""\s*:\s*""([^""]+)""");
                                if (match.Success)
                                {
                                    toolPathInfo[id] = match.Groups[1].Value;
                                    target = $"{fnName}('{Path.GetFileName(match.Groups[1].Value)}')";
                                }
                            }
                            else if (args.Contains("\"command\":"))
                            {
                                var match = Regex.Match(args, @"""command""\s*:\s*""([^""]+)""");
                                if (match.Success)
                                {
                                    var cmd = match.Groups[1].Value;
                                    if (cmd.Length > 25) cmd = cmd.Substring(0, 25) + "...";
                                    target = $"{fnName}('{cmd}')";
                                }
                            }
                            toolCallInfo[id] = target;
                        }
                    }
                }
            }

            for (int i = 2; i < cutoff; i++)
            {
                var m = messages[i];
                var role = m?["role"]?.GetValue<string>();

                // 1. Tool result message: { "role": "tool", "tool_call_id": "...", "content": "..." }
                if (role == "tool")
                {
                    var contentNode = m?["content"];
                    if (contentNode != null)
                    {
                        var text = contentNode.GetValue<string?>();
                        if (!string.IsNullOrEmpty(text) && text.Length > 400 && !text.StartsWith("[AST Skeleton") && !text.StartsWith("[Output of"))
                        {
                            var toolId = m?["tool_call_id"]?.GetValue<string>() ?? "";
                            toolCallInfo.TryGetValue(toolId, out var label);
                            label ??= "tool execution";
                            toolPathInfo.TryGetValue(toolId, out var targetPath);

                            if (m != null)
                            {
                                if (!string.IsNullOrEmpty(targetPath) && IsCodeFile(targetPath))
                                {
                                    var skeleton = _astService.ExtractFileSkeleton(targetPath, text);
                                    m["content"] = $"[AST Skeleton of {Path.GetFileName(targetPath)} (Full file {text.Length:N0} chars on disk; inner bodies collapsed for context efficiency)]:\n{skeleton}";
                                }
                                else
                                {
                                    if (text.Length > 2000)
                                    {
                                        m["content"] = $"[Output of {label} ({text.Length:N0} chars; diagnostics summarized)]:\n" + SliceDiagnosticOutput(text, 10, 25);
                                    }
                                    else
                                    {
                                        m["content"] = $"[Output of {label} ({text.Length:N0} chars) pruned from context history to maintain 27B speed. File or command result is on disk.]";
                                    }
                                }
                                prunedAny = true;
                            }
                        }
                    }
                }
                // 2. Giant command output or snapshot inside user message (e.g. system-reminder)
                else if (role == "user")
                {
                    var content = m?["content"]?.GetValue<string?>();
                    if (!string.IsNullOrEmpty(content) && content.Length > 3000 && m != null)
                    {
                        m["content"] = SliceDiagnosticOutput(content, 15, 30);
                        prunedAny = true;
                    }
                }
                // 3. Historical assistant file writes (e.g. write_file with 20k-50k chars of code in arguments)
                else if (role == "assistant")
                {
                    var toolCalls = m?["tool_calls"]?.AsArray();
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            var fn = tc?["function"];
                            var fnName = fn?["name"]?.GetValue<string>() ?? "";
                            var rawArgs = fn?["arguments"]?.GetValue<string>() ?? "";

                            if ((fnName == "write_file" || fnName == "edit_file") && rawArgs.Length > 600)
                            {
                                try
                                {
                                    var argsNode = JsonNode.Parse(rawArgs);
                                    if (argsNode != null)
                                    {
                                        bool argsModified = false;
                                        if (argsNode["content"] != null)
                                        {
                                            var contentStr = argsNode["content"]?.GetValue<string?>() ?? "";
                                            if (contentStr.Length > 400 && !contentStr.StartsWith("[File content"))
                                            {
                                                var filePath = argsNode["file_path"]?.GetValue<string?>() ?? argsNode["path"]?.GetValue<string?>() ?? "file";
                                                argsNode["content"] = $"[File content of {Path.GetFileName(filePath)} ({contentStr.Length:N0} chars) written to disk in earlier turn.]";
                                                argsModified = true;
                                            }
                                        }
                                        if (argsNode["new_string"] != null)
                                        {
                                            var newStr = argsNode["new_string"]?.GetValue<string?>() ?? "";
                                            if (newStr.Length > 500 && !newStr.StartsWith("[Edit content"))
                                            {
                                                argsNode["new_string"] = $"[Edit content ({newStr.Length:N0} chars) applied to disk in earlier turn.]";
                                                argsModified = true;
                                            }
                                        }

                                        if (argsModified && fn != null)
                                        {
                                            fn["arguments"] = argsNode.ToJsonString();
                                            prunedAny = true;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            // 4. Working memory safety gate: If ANY tool output in recent turns exceeds 20,000 chars
            // (e.g. runaway compiler loops, infinite log dumps), slice it with Head + Tail preservation so it cannot crash the KV cache.
            for (int i = Math.Max(0, cutoff); i < messages.Count; i++)
            {
                var m = messages[i];
                if (m?["role"]?.GetValue<string>() == "tool")
                {
                    var contentNode = m?["content"];
                    if (contentNode != null)
                    {
                        var text = contentNode.GetValue<string?>();
                        if (!string.IsNullOrEmpty(text) && text.Length > 20000)
                        {
                            var toolId = m?["tool_call_id"]?.GetValue<string>() ?? "";
                            toolPathInfo.TryGetValue(toolId, out var targetPath);
                            if (m != null && (string.IsNullOrEmpty(targetPath) || !IsCodeFile(targetPath)))
                            {
                                m["content"] = SliceDiagnosticOutput(text, 20, 50);
                                prunedAny = true;
                            }
                        }
                    }
                }
            }

            if (prunedAny)
            {
                var newJson = root!.ToJsonString();
                int newEstimatedTokens = newJson.Length / 4;
                int savedTokens = estimatedTokens - newEstimatedTokens;

                OnPruned?.Invoke(estimatedTokens, newEstimatedTokens, savedTokens);
                return Encoding.UTF8.GetBytes(newJson);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pruning error: {ex.Message}");
        }

        return Encoding.UTF8.GetBytes(rawJson);
    }

    private static string SliceDiagnosticOutput(string text, int headLines = 15, int tailLines = 35)
    {
        var lines = text.Split('\n');
        if (lines.Length <= headLines + tailLines + 5)
        {
            if (text.Length <= 4000) return text;
            return text.Substring(0, 1500) + $"\n\n[... {text.Length - 3000:N0} chars omitted for context efficiency ...]\n\n" + text.Substring(text.Length - 1500);
        }

        var head = string.Join("\n", lines.Take(headLines));
        var tail = string.Join("\n", lines.Skip(lines.Length - tailLines));
        int omitted = lines.Length - headLines - tailLines;
        return $"{head}\n\n[... {omitted} lines ({text.Length:N0} chars) omitted for context efficiency ...]\n\n{tail}";
    }

    private static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".css" or ".py" or ".json";
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            if (_listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _upstreamClient.Dispose();
    }
}
