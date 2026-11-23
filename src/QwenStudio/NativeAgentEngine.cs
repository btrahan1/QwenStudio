using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QwenStudio;

public sealed class NativeAgentEngine : IDisposable
{
    private readonly AstService _astService;
    private readonly Action<object> _postToUi;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _activeCts;
    private readonly List<JsonObject> _messageHistory = new();
    private bool _isRunning;
    private string? _currentSessionId;

    public bool IsRunning => _isRunning;
    public string? CurrentSessionId => _currentSessionId;

    public NativeAgentEngine(AstService astService, Action<object> postToUi)
    {
        _astService = astService;
        _postToUi = postToUi;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Stop()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch { }
        _isRunning = false;
    }

    public void ClearSession()
    {
        Stop();
        lock (_messageHistory)
        {
            _messageHistory.Clear();
        }
        _currentSessionId = null;
    }

    public async Task RunPromptAsync(
        string workspace,
        string prompt,
        ProviderProfile profile,
        StudioSettings settings)
    {
        if (_isRunning) Stop();

        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        _isRunning = true;

        if (string.IsNullOrEmpty(_currentSessionId))
        {
            _currentSessionId = Guid.NewGuid().ToString();
        }

        // Refresh WORKSPACE_SKELETON.md before prompt so model always has an up-to-date repo map
        try
        {
            var repoMap = _astService.GenerateWorkspaceRepoMap(workspace);
            if (!string.IsNullOrWhiteSpace(repoMap))
            {
                File.WriteAllText(Path.Combine(workspace, "WORKSPACE_SKELETON.md"), repoMap, Encoding.UTF8);
            }
        }
        catch { }

        _postToUi(new
        {
            type = "agentStarted",
            prompt,
            provider = profile.Name + " (Native Engine)",
            model = profile.Model
        });

        try
        {
            lock (_messageHistory)
            {
                if (_messageHistory.Count == 0)
                {
                    _messageHistory.Add(BuildSystemMessage(workspace, settings.TestMode));
                }

                _messageHistory.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                });
            }

            int turnCount = 0;
            const int maxTurns = 30;

            while (turnCount++ < maxTurns && !ct.IsCancellationRequested)
            {
                // In-memory context pruning before sending to the model
                PruneMessageHistoryInMemory(settings.InFlightPruningThreshold);

                var (content, thinking, toolCalls) = await DispatchTurnAsync(profile, ct);

                if (ct.IsCancellationRequested) break;

                // If the model produced tool calls, execute them and continue loop
                if (toolCalls != null && toolCalls.Count > 0)
                {
                    // Record assistant message with tool calls
                    var assistantMsg = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content
                    };

                    var tcArray = new JsonArray();
                    foreach (var tc in toolCalls)
                    {
                        tcArray.Add(new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = tc.Arguments
                            }
                        });
                    }
                    assistantMsg["tool_calls"] = tcArray;

                    lock (_messageHistory)
                    {
                        _messageHistory.Add(assistantMsg);
                    }

                    // Execute tool calls natively (Parallel dispatch for read-only tools, sequential for writes)
                    bool allReadOnly = toolCalls.All(tc => tc.Name is "read_file" or "grep_search" or "file_search");

                    if (allReadOnly && toolCalls.Count > 1)
                    {
                        foreach (var tc in toolCalls)
                        {
                            PostToolUseStart(tc.Name, tc.Id);
                        }

                        var parallelTasks = toolCalls.Select(async tc =>
                        {
                            var sw = Stopwatch.StartNew();
                            var res = await ExecuteToolAsync(tc.Name, tc.Arguments, workspace, ct);
                            sw.Stop();
                            return (tc, res);
                        }).ToList();

                        var results = await Task.WhenAll(parallelTasks);

                        foreach (var (tc, res) in results)
                        {
                            PostToolResult(res.Output, res.Success);
                            lock (_messageHistory)
                            {
                                _messageHistory.Add(new JsonObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = tc.Id,
                                    ["name"] = tc.Name,
                                    ["content"] = res.Output
                                });
                            }
                        }
                    }
                    else
                    {
                        foreach (var tc in toolCalls)
                        {
                            if (ct.IsCancellationRequested) break;

                            PostToolUseStart(tc.Name, tc.Id);

                            var sw = Stopwatch.StartNew();
                            var result = await ExecuteToolAsync(tc.Name, tc.Arguments, workspace, ct);
                            sw.Stop();

                            PostToolResult(result.Output, result.Success);

                            // P0 Proactive Write Eviction: Once written to disk, disk is ground truth.
                            // Compact raw file contents in the in-memory assistant message so KV cache doesn't bloat.
                            if (result.Success && (tc.Name == "write_file" || tc.Name == "edit_file"))
                            {
                                CompactToolCallArguments(assistantMsg, tc.Id);
                            }

                            lock (_messageHistory)
                            {
                                _messageHistory.Add(new JsonObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = tc.Id,
                                    ["name"] = tc.Name,
                                    ["content"] = result.Output
                                });
                            }
                        }
                    }
                }
                else
                {
                    // No more tool calls; the assistant completed its answer
                    var assistantMsg = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content
                    };
                    lock (_messageHistory)
                    {
                        _messageHistory.Add(assistantMsg);
                    }
                    break;
                }
            }

            _postToUi(new { type = "agentCompleted", exitCode = 0, sessionId = _currentSessionId });
        }
        catch (OperationCanceledException)
        {
            _postToUi(new { type = "agentStopped", message = "Agent stopped by user." });
        }
        catch (Exception ex)
        {
            _postToUi(new { type = "agentError", message = ex.Message });
        }
        finally
        {
            _isRunning = false;
        }
    }

    private JsonObject BuildSystemMessage(string workspace, string testMode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert autonomous software engineer operating inside QwenStudio.");
        sb.AppendLine($"Working Directory: {workspace}");
        sb.AppendLine();
        sb.AppendLine("CORE PRINCIPLES & GUIDELINES:");
        sb.AppendLine("1. Be concise, direct, and fact-based. Avoid excessive conversational filler.");
        sb.AppendLine("2. WORKSPACE SKELETON: A complete AST symbol map of the repository is available at `WORKSPACE_SKELETON.md`.");
        sb.AppendLine("   Always inspect `WORKSPACE_SKELETON.md` first before reading entire files.");
        sb.AppendLine("3. When inspecting files, prefer targeted reads with `offset` and `limit` line numbers rather than reading entire large files.");
        sb.AppendLine("4. When editing files, prefer `edit_file` with exact `old_string` and `new_string` blocks. Ensure `old_string` matches exact indentation and whitespace.");
        sb.AppendLine();

        switch (testMode?.ToLowerInvariant())
        {
            case "none":
                sb.AppendLine("VERIFICATION POLICY: Direct execution mode (No Tests).");
                sb.AppendLine("- Focus purely on writing clean, high-quality code directly to disk.");
                sb.AppendLine("- Do not write automated test scripts or run test suites; conclude promptly once all code files are written.");
                break;

            case "rigorous":
                sb.AppendLine("VERIFICATION POLICY: Rigorous autonomous testing mode.");
                sb.AppendLine("- You are required to autonomously verify your work before declaring completion.");
                sb.AppendLine("- Write a dedicated automated smoke or unit test script (e.g. `tests/smoke-test.js` or project test runner).");
                sb.AppendLine("- Execute the test suite using `run_command`.");
                sb.AppendLine("- If any test fails, analyze the failure, apply targeted fixes using `edit_file`, and re-test.");
                sb.AppendLine("- Iterate autonomously until all tests pass cleanly before concluding.");
                break;

            case "fast":
            default:
                sb.AppendLine("VERIFICATION POLICY: Fast sanity checks mode.");
                sb.AppendLine("- Before concluding, perform rapid static sanity checks:");
                sb.AppendLine("  * UI Contract alignment: Verify that element IDs, classes, and listener names in JavaScript match the HTML elements exactly.");
                sb.AppendLine("  * CSS specificity: Ensure `[hidden]` has `display: none !important;` so layout rules (`display: flex/grid`) do not override it.");
                sb.AppendLine("  * Syntax sanity: If appropriate, perform a single fast non-interactive check (e.g. `node -c app.js` or `dotnet build`) to confirm zero syntax errors.");
                sb.AppendLine("- Do not write a 300-line test framework; keep verification fast and lightweight.");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("Think step-by-step using <thought>...</thought> blocks before executing tools.");

        return new JsonObject
        {
            ["role"] = "system",
            ["content"] = sb.ToString()
        };
    }

    private static JsonArray GetToolDefinitions()
    {
        return new JsonArray
        {
            // read_file
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read the contents of a file. Supports line range offset and limit to inspect only relevant portions.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute or relative path to the file." },
                            ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional 1-based start line number." },
                            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional maximum number of lines to read." }
                        },
                        ["required"] = new JsonArray { "file_path" }
                    }
                }
            },
            // write_file
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write_file",
                    ["description"] = "Create a new file or completely overwrite an existing file with new content.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the file to create or overwrite." },
                            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "The complete content to write into the file." }
                        },
                        ["required"] = new JsonArray { "file_path", "content" }
                    }
                }
            },
            // edit_file
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "edit_file",
                    ["description"] = "Perform an exact string replacement in a file. `old_string` must match existing code including whitespace and indentation.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the file to edit." },
                            ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "The exact existing code to be replaced." },
                            ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "The replacement code." }
                        },
                        ["required"] = new JsonArray { "file_path", "old_string", "new_string" }
                    }
                }
            },
            // run_command
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "run_command",
                    ["description"] = "Execute a command in PowerShell in the workspace directory (e.g. dotnet build, npm test, git status).",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The exact command to run." }
                        },
                        ["required"] = new JsonArray { "command" }
                    }
                }
            },
            // grep_search
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "grep_search",
                    ["description"] = "Fast regex or text search across workspace files.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Text or regex pattern to search for." },
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Subdirectory to search in (defaults to workspace root)." },
                            ["include"] = new JsonObject { ["type"] = "string", ["description"] = "Optional file glob filter, e.g. '*.cs' or '*.js'." }
                        },
                        ["required"] = new JsonArray { "query" }
                    }
                }
            },
            // file_search
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "file_search",
                    ["description"] = "Search for file paths matching a wildcard pattern.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Wildcard pattern, e.g. '*Model*.cs' or '*.html'." },
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Subdirectory to search in (defaults to workspace root)." }
                        },
                        ["required"] = new JsonArray { "pattern" }
                    }
                }
            }
        };
    }

    private async Task<(string Content, string Thinking, List<ToolCallItem> ToolCalls)> DispatchTurnAsync(
        ProviderProfile profile,
        CancellationToken ct)
    {
        var baseUrl = profile.BaseUrl.TrimEnd('/');
        if (profile.IsLocal)
        {
            // Connect directly to llama-server on port 8081
            baseUrl = "http://127.0.0.1:8081/v1";
        }
        var endpoint = $"{baseUrl}/chat/completions";

        var msgArray = new JsonArray();
        lock (_messageHistory)
        {
            foreach (var m in _messageHistory)
            {
                msgArray.Add(JsonNode.Parse(m.ToJsonString()));
            }
        }

        var payload = new JsonObject
        {
            ["model"] = profile.Model,
            ["messages"] = msgArray,
            ["tools"] = GetToolDefinitions(),
            ["stream"] = true,
            ["temperature"] = 0.2
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(profile.ApiKey) && profile.ApiKey != "local-no-key-needed")
        {
            request.Headers.Add("Authorization", $"Bearer {profile.ApiKey}");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        var fullContent = new StringBuilder();
        var fullThinking = new StringBuilder();
        var toolCallsAcc = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        bool insideThought = false;

        // Micro-batching buffers (25ms flush window) to throttle IPC overhead across WebView2
        var thinkingBatch = new StringBuilder();
        var contentBatch = new StringBuilder();
        long lastFlushTicks = Stopwatch.GetTimestamp();

        void FlushThinking(bool force = false)
        {
            if (thinkingBatch.Length == 0) return;
            if (force || Stopwatch.GetElapsedTime(lastFlushTicks).TotalMilliseconds >= 25 || thinkingBatch.ToString().Contains('\n'))
            {
                PostThinkingChunk(thinkingBatch.ToString());
                thinkingBatch.Clear();
                lastFlushTicks = Stopwatch.GetTimestamp();
            }
        }

        void FlushContent(bool force = false)
        {
            if (contentBatch.Length == 0) return;
            if (force || Stopwatch.GetElapsedTime(lastFlushTicks).TotalMilliseconds >= 25 || contentBatch.ToString().Contains('\n'))
            {
                PostContentChunk(contentBatch.ToString());
                contentBatch.Clear();
                lastFlushTicks = Stopwatch.GetTimestamp();
            }
        }

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line.Substring(6).Trim();
            if (data == "[DONE]") break;

            try
            {
                var chunk = JsonNode.Parse(data);
                var delta = chunk?["choices"]?[0]?["delta"];
                if (delta == null) continue;

                // Handle reasoning / thought tokens (e.g. from DeepSeek or Qwen <thought> tags)
                var reasoningContent = delta["reasoning_content"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(reasoningContent))
                {
                    fullThinking.Append(reasoningContent);
                    thinkingBatch.Append(reasoningContent);
                    FlushThinking(force: false);
                }

                var contentChunk = delta["content"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(contentChunk))
                {
                    // Check for embedded <thought>...</thought> tags in text models
                    if (contentChunk.Contains("<thought>")) insideThought = true;

                    if (insideThought)
                    {
                        fullThinking.Append(contentChunk);
                        thinkingBatch.Append(contentChunk);
                        FlushThinking(force: contentChunk.Contains("</thought>"));
                        if (contentChunk.Contains("</thought>")) insideThought = false;
                    }
                    else
                    {
                        fullContent.Append(contentChunk);
                        contentBatch.Append(contentChunk);
                        FlushContent(force: false);
                    }
                }

                // Handle streaming tool_calls
                var deltaTools = delta["tool_calls"]?.AsArray();
                if (deltaTools != null)
                {
                    foreach (var dt in deltaTools)
                    {
                        int index = dt?["index"]?.GetValue<int>() ?? 0;
                        if (!toolCallsAcc.TryGetValue(index, out var item))
                        {
                            item = ("", "", new StringBuilder());
                            toolCallsAcc[index] = item;
                        }

                        var id = dt?["id"]?.GetValue<string?>();
                        if (!string.IsNullOrEmpty(id)) item.Id = id;

                        var fn = dt?["function"];
                        if (fn != null)
                        {
                            var fnName = fn["name"]?.GetValue<string?>();
                            if (!string.IsNullOrEmpty(fnName)) item.Name = fnName;

                            var fnArgs = fn["arguments"]?.GetValue<string?>();
                            if (!string.IsNullOrEmpty(fnArgs)) item.Args.Append(fnArgs);
                        }

                        toolCallsAcc[index] = item;
                    }
                }
            }
            catch { }
        }

        // Flush any remaining buffered tokens
        FlushThinking(force: true);
        FlushContent(force: true);

        var resultTools = new List<ToolCallItem>();
        foreach (var kvp in toolCallsAcc)
        {
            var item = kvp.Value;
            var id = string.IsNullOrEmpty(item.Id) ? $"call_{Guid.NewGuid():N}" : item.Id;
            resultTools.Add(new ToolCallItem(id, item.Name, item.Args.ToString()));
        }

        return (fullContent.ToString(), fullThinking.ToString(), resultTools);
    }

    private async Task<(bool Success, string Output)> ExecuteToolAsync(
        string name,
        string argsJson,
        string workspace,
        CancellationToken ct)
    {
        try
        {
            var args = JsonNode.Parse(argsJson);
            switch (name)
            {
                case "read_file":
                {
                    var filePath = args?["file_path"]?.GetValue<string?>();
                    if (string.IsNullOrWhiteSpace(filePath)) return (false, "Error: file_path argument required.");

                    var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(workspace, filePath);
                    if (!File.Exists(fullPath)) return (false, $"Error: File '{filePath}' does not exist.");

                    var offset = args?["offset"]?.GetValue<int?>() ?? 1;
                    var limit = args?["limit"]?.GetValue<int?>();

                    var lines = await File.ReadAllLinesAsync(fullPath, ct);
                    if (offset > lines.Length) return (false, $"Error: offset {offset} exceeds file line count ({lines.Length}).");

                    int startIdx = Math.Max(0, offset - 1);
                    int count = limit.HasValue ? Math.Min(limit.Value, lines.Length - startIdx) : lines.Length - startIdx;

                    var sb = new StringBuilder();
                    for (int i = 0; i < count; i++)
                    {
                        sb.AppendLine($"{startIdx + i + 1,5} | {lines[startIdx + i]}");
                    }
                    return (true, sb.ToString());
                }

                case "write_file":
                {
                    var filePath = args?["file_path"]?.GetValue<string?>();
                    var content = args?["content"]?.GetValue<string?>();
                    if (string.IsNullOrWhiteSpace(filePath)) return (false, "Error: file_path argument required.");
                    if (content == null) return (false, "Error: content argument required.");

                    var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(workspace, filePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
                    return (true, $"File '{Path.GetFileName(filePath)}' written successfully ({content.Length:N0} characters).");
                }

                case "edit_file":
                {
                    var filePath = args?["file_path"]?.GetValue<string?>();
                    var oldString = args?["old_string"]?.GetValue<string?>();
                    var newString = args?["new_string"]?.GetValue<string?>();

                    if (string.IsNullOrWhiteSpace(filePath)) return (false, "Error: file_path argument required.");
                    if (oldString == null || newString == null) return (false, "Error: old_string and new_string required.");

                    var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(workspace, filePath);
                    if (!File.Exists(fullPath)) return (false, $"Error: File '{filePath}' does not exist.");

                    var fileContent = await File.ReadAllTextAsync(fullPath, ct);

                    // 1. Exact match
                    if (fileContent.Contains(oldString))
                    {
                        fileContent = fileContent.Replace(oldString, newString);
                        await File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8, ct);
                        return (true, $"Successfully replaced targeted code block in '{Path.GetFileName(filePath)}'.");
                    }

                    // 2. CRLF vs LF normalized match
                    var normFile = fileContent.Replace("\r\n", "\n");
                    var normOld = oldString.Replace("\r\n", "\n");
                    var normNew = newString.Replace("\r\n", "\n");

                    if (normFile.Contains(normOld))
                    {
                        normFile = normFile.Replace(normOld, normNew);
                        // Restore Windows CRLF
                        await File.WriteAllTextAsync(fullPath, normFile.Replace("\n", "\r\n"), Encoding.UTF8, ct);
                        return (true, $"Successfully replaced code block in '{Path.GetFileName(filePath)}' (newline-normalized).");
                    }

                    return (false, $"Error: `old_string` was not found in '{Path.GetFileName(filePath)}'. Inspect the file lines first using `read_file` to ensure exact whitespace match.");
                }

                case "run_command":
                {
                    var cmd = args?["command"]?.GetValue<string?>();
                    if (string.IsNullOrWhiteSpace(cmd)) return (false, "Error: command argument required.");

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd.Replace("\"", "\\\"")}\"",
                        WorkingDirectory = workspace,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    using var proc = new Process { StartInfo = psi };
                    proc.Start();

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    try
                    {
                        var outputTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
                        var errorTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

                        await proc.WaitForExitAsync(linkedCts.Token);
                        var stdout = await outputTask;
                        var stderr = await errorTask;

                        var combined = (stdout + "\n" + stderr).Trim();
                        if (combined.Length > 4000)
                        {
                            combined = SliceDiagnosticOutput(combined, 15, 35);
                        }
                        return (proc.ExitCode == 0, combined);
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill(entireProcessTree: true);
                            }
                        }
                        catch { }

                        if (ct.IsCancellationRequested)
                        {
                            return (false, "Command cancelled by user.");
                        }

                        return (false, "Command timed out after 45 seconds and was terminated. Long-running servers or tests must run asynchronously or exit promptly.");
                    }
                }

                case "grep_search":
                {
                    var query = args?["query"]?.GetValue<string?>();
                    var relPath = args?["path"]?.GetValue<string?>() ?? "";
                    var include = args?["include"]?.GetValue<string?>() ?? "*.*";

                    if (string.IsNullOrWhiteSpace(query)) return (false, "Error: query required.");

                    var searchDir = string.IsNullOrWhiteSpace(relPath) ? workspace : Path.Combine(workspace, relPath);
                    if (!Directory.Exists(searchDir)) return (false, $"Error: Directory '{relPath}' does not exist.");

                    var results = new List<string>();
                    var files = Directory.GetFiles(searchDir, include, SearchOption.AllDirectories);

                    foreach (var f in files)
                    {
                        if (IsIgnoredPath(f)) continue;
                        try
                        {
                            var lines = await File.ReadAllLinesAsync(f, ct);
                            for (int i = 0; i < lines.Length; i++)
                            {
                                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    var displayPath = Path.GetRelativePath(workspace, f);
                                    results.Add($"{displayPath}:{i + 1}: {lines[i].Trim()}");
                                    if (results.Count >= 50) break;
                                }
                            }
                        }
                        catch { }
                        if (results.Count >= 50) break;
                    }

                    if (results.Count == 0) return (true, $"No matches found for '{query}'.");
                    return (true, string.Join("\n", results));
                }

                case "file_search":
                {
                    var pattern = args?["pattern"]?.GetValue<string?>() ?? "*";
                    var relPath = args?["path"]?.GetValue<string?>() ?? "";

                    var searchDir = string.IsNullOrWhiteSpace(relPath) ? workspace : Path.Combine(workspace, relPath);
                    if (!Directory.Exists(searchDir)) return (false, $"Error: Directory '{relPath}' does not exist.");

                    var files = Directory.GetFiles(searchDir, pattern, SearchOption.AllDirectories)
                        .Where(f => !IsIgnoredPath(f))
                        .Take(50)
                        .Select(f => Path.GetRelativePath(workspace, f));

                    return (true, string.Join("\n", files));
                }

                default:
                    return (false, $"Error: Unknown tool '{name}'.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error executing tool {name}: {ex.Message}");
        }
    }

    private void PruneMessageHistoryInMemory(int thresholdTokens)
    {
        lock (_messageHistory)
        {
            if (_messageHistory.Count < 10) return;

            int rawLength = _messageHistory.Sum(m => m.ToJsonString().Length);
            int estimatedTokens = rawLength / 4;
            if (estimatedTokens < thresholdTokens) return;

            int cutoff = _messageHistory.Count - 8;
            for (int i = 1; i < cutoff; i++)
            {
                var m = _messageHistory[i];
                var role = m["role"]?.GetValue<string>();

                if (role == "tool")
                {
                    var content = m["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(content) && content.Length > 500 && !content.StartsWith("[AST Skeleton"))
                    {
                        var toolName = m["name"]?.GetValue<string>() ?? "tool";
                        m["content"] = $"[Output of {toolName} ({content.Length:N0} chars) pruned from memory. Details on disk.]";
                    }
                }
                else if (role == "assistant")
                {
                    var toolCalls = m["tool_calls"]?.AsArray();
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            var fn = tc?["function"];
                            var fnName = fn?["name"]?.GetValue<string>();
                            var rawArgs = fn?["arguments"]?.GetValue<string>() ?? "";

                            if ((fnName == "write_file" || fnName == "edit_file") && rawArgs.Length > 600)
                            {
                                try
                                {
                                    var argsNode = JsonNode.Parse(rawArgs);
                                    if (argsNode?["content"] != null)
                                    {
                                        argsNode["content"] = "[File content written to disk in earlier turn.]";
                                        fn!["arguments"] = argsNode.ToJsonString();
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }
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

    private static bool IsIgnoredPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is ".git" or "node_modules" or "bin" or "obj" or ".vs" or ".qwen" or "__pycache__");
    }

    private static void CompactToolCallArguments(JsonObject assistantMsg, string toolCallId)
    {
        var toolCalls = assistantMsg["tool_calls"]?.AsArray();
        if (toolCalls == null) return;

        foreach (var tc in toolCalls)
        {
            if (tc?["id"]?.GetValue<string>() == toolCallId)
            {
                var fn = tc["function"];
                var rawArgs = fn?["arguments"]?.GetValue<string>();
                if (string.IsNullOrEmpty(rawArgs)) continue;

                try
                {
                    var node = JsonNode.Parse(rawArgs);
                    if (node is JsonObject obj)
                    {
                        bool modified = false;
                        if (obj["content"] != null && obj["content"]!.GetValue<string>().Length > 300)
                        {
                            var filePath = obj["file_path"]?.GetValue<string>() ?? "file";
                            obj["content"] = $"[Content written to disk ({Path.GetFileName(filePath)}). Ground truth remains on disk.]";
                            modified = true;
                        }
                        if (obj["new_string"] != null && obj["new_string"]!.GetValue<string>().Length > 300)
                        {
                            var filePath = obj["file_path"]?.GetValue<string>() ?? "file";
                            obj["new_string"] = $"[Edit applied to disk ({Path.GetFileName(filePath)}). Ground truth remains on disk.]";
                            modified = true;
                        }

                        if (modified)
                        {
                            fn!["arguments"] = obj.ToJsonString();
                        }
                    }
                }
                catch { }
                break;
            }
        }
    }

    private void PostThinkingChunk(string chunk)
    {
        _postToUi(new
        {
            type = "qwenStream",
            raw = "",
            packet = new
            {
                type = "stream_event",
                @event = new
                {
                    type = "content_block_delta",
                    delta = new
                    {
                        type = "thinking_delta",
                        thinking = chunk
                    }
                }
            }
        });
    }

    private void PostContentChunk(string chunk)
    {
        _postToUi(new
        {
            type = "qwenStream",
            raw = "",
            packet = new
            {
                type = "stream_event",
                @event = new
                {
                    type = "content_block_delta",
                    delta = new
                    {
                        type = "text_delta",
                        text = chunk
                    }
                }
            }
        });
    }

    private void PostToolUseStart(string toolName, string id)
    {
        _postToUi(new
        {
            type = "qwenStream",
            raw = "",
            packet = new
            {
                type = "stream_event",
                @event = new
                {
                    type = "content_block_start",
                    content_block = new
                    {
                        type = "tool_use",
                        name = toolName,
                        id = id
                    }
                }
            }
        });
    }

    private void PostToolResult(string output, bool success)
    {
        _postToUi(new
        {
            type = "qwenStream",
            raw = "",
            packet = new
            {
                type = "user",
                message = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "tool_result",
                            is_error = !success,
                            content = output
                        }
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}

public sealed record ToolCallItem(string Id, string Name, string Arguments);
