using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QwenStudio;

public sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly HttpClient _modelLoaderClient = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HashSet<string> _loadingModels = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPollingTelemetry;
    private readonly System.Windows.Forms.Timer _telemetryTimer;
    private FileSystemWatcher? _workspaceWatcher;
    private Process? _activeQwenProcess;
    private readonly InFlightContextProxy _proxy;
    private readonly AstService _astService = new();
    private readonly NativeAgentEngine _nativeAgentEngine;

    private string _currentWorkspace = @"C:\Users\Bart\Projects\webcontacts";
    private string? _currentSessionId;
    private StudioSettings _settings;

    private int _lastDecodedTokens;
    private DateTime _lastTelemetryTime = DateTime.UtcNow;
    private double _smoothedTps;

    private int _lastKnownNPrompt;
    private int _lastKnownNCtx = 65536;
    private bool _isCompacting;
    private bool _userCancelled;

    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QwenStudio",
        "studio-settings.json");

    public MainForm()
    {
        Text = "QwenStudio — Local & Cloud Agentic IDE";
        Width = 1600;
        Height = 960;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 23);

        _settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(_settings.LastWorkspace) && Directory.Exists(_settings.LastWorkspace))
        {
            _currentWorkspace = _settings.LastWorkspace;
        }
        else if (!Directory.Exists(_currentWorkspace))
        {
            _currentWorkspace = @"C:\Projects";
        }

        _proxy = new InFlightContextProxy("http://127.0.0.1:8081", 8082)
        {
            IsPruningEnabled = () => _settings.InFlightPruningEnabled,
            GetPruningThresholdTokens = () => _settings.InFlightPruningThreshold,
            OnPruned = (orig, pruned, saved) =>
            {
                BeginInvoke(() =>
                {
                    PostToUi(new
                    {
                        type = "inFlightPruning",
                        originalTokens = orig,
                        prunedTokens = pruned,
                        savedTokens = saved
                    });
                });
            }
        };
        _proxy.Start();
        _nativeAgentEngine = new NativeAgentEngine(_astService, PostToUi);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(15, 17, 23)
        };
        Controls.Add(_webView);

        _telemetryTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _telemetryTimer.Tick += async (_, _) => await PollLlamaServerAsync();

        Load += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_currentSessionId))
            {
                _settings.LastSessionId = _currentSessionId;
                _settings.LastWorkspace = _currentWorkspace;
                SaveSettings(_settings);
            }
            StopActiveQwenProcess();
            _nativeAgentEngine?.Dispose();
            _proxy?.Dispose();
            _workspaceWatcher?.Dispose();
            _telemetryTimer.Stop();
        };
    }

    private async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QwenStudio",
            "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "qwenstudio.local",
            wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.Source = new Uri("https://qwenstudio.local/index.html");
        SetupWorkspaceWatcher(_currentWorkspace);
        Task.Run(() =>
        {
            SanitizeGlobalMemories();
            GenerateWorkspaceSkeleton(_currentWorkspace);
        });
        _telemetryTimer.Start();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = JsonNode.Parse(e.WebMessageAsJson);
            var action = json?["action"]?.GetValue<string>();
            if (string.IsNullOrEmpty(action)) return;

            switch (action)
            {
                case "ready":
                    SendInitialState();
                    break;

                case "pickFolder":
                    PickWorkspaceFolder();
                    break;

                case "setWorkspace":
                    var path = json?["path"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        SetWorkspace(path);
                    }
                    break;

                case "refreshFiles":
                    SendFileTree();
                    break;

                case "readFile":
                    var filePath = json?["path"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        ReadAndSendFile(filePath);
                    }
                    break;

                case "saveFile":
                    var savePath = json?["path"]?.GetValue<string>();
                    var content = json?["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(savePath) && content != null)
                    {
                        SaveFileToDisk(savePath, content);
                    }
                    break;

                case "sendPrompt":
                    var prompt = json?["prompt"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        if (_settings.AgentEngine == "native")
                        {
                            _ = Task.Run(async () => await RunNativePromptAsync(prompt).ConfigureAwait(false));
                        }
                        else
                        {
                            _ = Task.Run(async () => await RunQwenPromptAsync(prompt).ConfigureAwait(false));
                        }
                    }
                    break;

                case "stopPrompt":
                    StopActiveQwenProcess();
                    PostToUi(new { type = "agentStopped" });
                    break;

                case "compactSession":
                    if (_activeQwenProcess != null && !_activeQwenProcess.HasExited)
                    {
                        PostToUi(new { type = "error", message = "Agent is currently busy. Wait for completion or stop it first." });
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(_currentSessionId))
                    {
                        PostToUi(new { type = "error", message = "No active session to compact." });
                        break;
                    }
                    _ = StartCompactionAsync("manual");
                    break;

                case "toggleAutoCompact":
                    var autoEnabled = json?["enabled"]?.GetValue<bool>() ?? true;
                    _settings.AutoCompact = autoEnabled;
                    SaveSettings(_settings);
                    PostToUi(new { type = "settingsSaved", settings = _settings });
                    break;

                case "newChat":
                    StopActiveQwenProcess();
                    _nativeAgentEngine?.ClearSession();
                    _currentSessionId = null;
                    _settings.LastSessionId = null;
                    SaveSettings(_settings);
                    Task.Run(() => SanitizeGlobalMemories());
                    PostToUi(new { type = "chatCleared", sessionId = (string?)null });
                    SendChatSessions();
                    break;

                case "loadSession":
                    var sessionId = json?["sessionId"]?.GetValue<string>();
                    var file = json?["file"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(file))
                    {
                        LoadSessionHistory(sessionId, file);
                    }
                    break;

                case "deleteSession":
                    var delFile = json?["file"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(delFile) && File.Exists(delFile))
                    {
                        try { File.Delete(delFile); } catch { }
                        SendChatSessions();
                    }
                    break;

                case "saveSettings":
                    var settingsNode = json?["settings"];
                    if (settingsNode != null)
                    {
                        var updated = JsonSerializer.Deserialize<StudioSettings>(settingsNode.ToJsonString());
                        if (updated != null)
                        {
                            _settings = updated;
                            _settings.LastWorkspace = _currentWorkspace;
                            SaveSettings(_settings);
                            PostToUi(new { type = "settingsSaved", settings = _settings });
                            _ = PollLlamaServerAsync();
                        }
                    }
                    break;

                case "openInExplorer":
                    Process.Start("explorer.exe", _currentWorkspace);
                    break;
            }
        }
        catch (Exception ex)
        {
            PostToUi(new { type = "error", message = ex.Message });
        }
    }

    private void SendInitialState()
    {
        var sessionToRestore = _settings.LastSessionId;
        var chatFile = !string.IsNullOrWhiteSpace(sessionToRestore)
            ? FindSessionFile(_currentWorkspace, sessionToRestore)
            : null;

        if (chatFile == null)
        {
            var recent = FindMostRecentSession(_currentWorkspace);
            if (recent != null)
            {
                sessionToRestore = recent.Value.sessionId;
                chatFile = recent.Value.filePath;
            }
        }

        _currentSessionId = sessionToRestore;

        PostToUi(new
        {
            type = "init",
            workspace = _currentWorkspace,
            sessionId = _currentSessionId,
            settings = _settings
        });
        SendFileTree();
        SendChatSessions();

        if (!string.IsNullOrWhiteSpace(_currentSessionId) && chatFile != null)
        {
            LoadSessionHistory(_currentSessionId, chatFile);
        }
    }

    private void PickWorkspaceFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_currentWorkspace) ? _currentWorkspace : @"C:\Projects",
            Description = "Select Project Workspace Folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            SetWorkspace(dialog.SelectedPath);
        }
    }

    private void SetWorkspace(string path)
    {
        StopActiveQwenProcess();
        _currentWorkspace = path;
        _settings.LastWorkspace = path;

        var recent = FindMostRecentSession(path);
        _currentSessionId = recent?.sessionId;
        _settings.LastSessionId = _currentSessionId;
        SaveSettings(_settings);

        SetupWorkspaceWatcher(path);
        PostToUi(new
        {
            type = "workspaceChanged",
            workspace = _currentWorkspace,
            sessionId = _currentSessionId
        });
        SendFileTree();
        SendChatSessions();
        Task.Run(() =>
        {
            SanitizeGlobalMemories();
            GenerateWorkspaceSkeleton(path);
        });

        if (recent != null)
        {
            LoadSessionHistory(recent.Value.sessionId, recent.Value.filePath);
        }
    }

    private void SanitizeGlobalMemories()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var memoriesDir = Path.Combine(userProfile, ".qwen", "memories");
            if (!Directory.Exists(memoriesDir)) return;

            var memoryMd = Path.Combine(memoriesDir, "MEMORY.md");
            var projectFilesToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(memoriesDir, "*.md"))
            {
                var filename = Path.GetFileName(file);
                if (filename.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var content = File.ReadAllText(file);
                    // Purge anything that is tagged as project memory or contains project structure / code
                    if (content.Contains("type: project") ||
                        content.Contains("category: project_introduction") ||
                        (content.Contains("usage_scenarios:") && content.Contains("project")) ||
                        content.Contains("index.html") ||
                        content.Contains("app.js") ||
                        content.Contains("Program.cs") ||
                        content.Contains("package.json") ||
                        content.Contains("Contacts27B"))
                    {
                        projectFilesToRemove.Add(filename);
                        File.Delete(file);
                    }
                }
                catch { }
            }

            if (File.Exists(memoryMd))
            {
                var lines = File.ReadAllLines(memoryMd);
                var keptLines = new List<string>();
                bool modified = false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    bool remove = false;
                    foreach (var removedFile in projectFilesToRemove)
                    {
                        if (line.Contains(removedFile, StringComparison.OrdinalIgnoreCase))
                        {
                            remove = true;
                            break;
                        }
                    }
                    if (!remove)
                    {
                        keptLines.Add(line);
                    }
                    else
                    {
                        modified = true;
                    }
                }

                if (modified || keptLines.Count != lines.Length)
                {
                    File.WriteAllLines(memoryMd, keptLines, Encoding.UTF8);
                }
            }
        }
        catch { }
    }

    private void GenerateWorkspaceSkeleton(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var repoMap = _astService.GenerateWorkspaceRepoMap(path);
            if (!string.IsNullOrWhiteSpace(repoMap))
            {
                var skeletonPath = Path.Combine(path, "WORKSPACE_SKELETON.md");
                File.WriteAllText(skeletonPath, repoMap, Encoding.UTF8);

                // Write native Qwen CLI rule for context efficiency and skeleton-first navigation
                try
                {
                    var rulesDir = Path.Combine(path, ".qwen", "rules");
                    Directory.CreateDirectory(rulesDir);
                    var ruleFile = Path.Combine(rulesDir, "00-context-efficiency.md");
                    var testPolicy = _settings.TestMode switch
                    {
                        "none" => "5. Verification Mode: Direct (No Tests). Do not write automated smoke-test scripts or invoke testing skills unless requested.\n",
                        "rigorous" => "5. Verification Mode: Rigorous. Write and execute automated smoke/unit tests before declaring completion.\n",
                        _ => "5. Verification Mode: Fast Sanity. Check HTML/JS contract alignment and CSS [hidden] specificity before concluding.\n"
                    };

                    var ruleContent = 
                        "# Workspace Context Efficiency Rules\n\n" +
                        "1. A complete architectural symbol map of this workspace is maintained at `WORKSPACE_SKELETON.md`.\n" +
                        "2. ALWAYS consult `WORKSPACE_SKELETON.md` first to locate classes, functions, and symbols before reading source files.\n" +
                        "3. When inspecting or modifying code, use targeted reads (`offset` and `limit` line ranges) around the specific function or method rather than reading entire large files.\n" +
                        "4. Never re-read full files that you have already inspected or created unless verifying changes.\n" +
                        testPolicy;
                    File.WriteAllText(ruleFile, ruleContent, Encoding.UTF8);
                }
                catch { }

                BeginInvoke(() => SendFileTree());
            }
        }
        catch { }
    }

    private void SetupWorkspaceWatcher(string path)
    {
        _workspaceWatcher?.Dispose();
        if (!Directory.Exists(path)) return;

        _workspaceWatcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler handler = (_, e) =>
        {
            if (IsIgnoredPath(e.FullPath)) return;
            BeginInvoke(() =>
            {
                SendFileTree();
                if (File.Exists(e.FullPath))
                {
                    PostToUi(new { type = "fileChangedOnDisk", path = e.FullPath });
                }
            });
        };

        _workspaceWatcher.Created += handler;
        _workspaceWatcher.Changed += handler;
        _workspaceWatcher.Deleted += handler;
        _workspaceWatcher.Renamed += (_, e) => BeginInvoke(SendFileTree);
    }

    private static bool IsIgnoredPath(string fullPath)
    {
        if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is ".git" or "node_modules" or "bin" or "obj" or ".vs" or ".qwen" or "__pycache__");
    }

    private void SendFileTree()
    {
        if (!Directory.Exists(_currentWorkspace)) return;

        var items = new List<object>();
        BuildFileList(_currentWorkspace, _currentWorkspace, items, 0);
        PostToUi(new { type = "fileTree", workspace = _currentWorkspace, items });
    }

    private void BuildFileList(string root, string current, List<object> output, int depth)
    {
        if (depth > 5 || output.Count > 500) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(current).OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dir);
                if (name is ".git" or "node_modules" or "bin" or "obj" or ".vs" or ".qwen" or "__pycache__")
                    continue;

                output.Add(new
                {
                    name,
                    path = dir,
                    relativePath = Path.GetRelativePath(root, dir),
                    isDirectory = true,
                    depth
                });
                BuildFileList(root, dir, output, depth + 1);
            }

            foreach (var file in Directory.GetFiles(current).OrderBy(Path.GetFileName))
            {
                var info = new FileInfo(file);
                output.Add(new
                {
                    name = info.Name,
                    path = file,
                    relativePath = Path.GetRelativePath(root, file),
                    isDirectory = false,
                    size = info.Length,
                    depth
                });
            }
        }
        catch { }
    }

    private void ReadAndSendFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024)
            {
                PostToUi(new
                {
                    type = "fileContent",
                    path,
                    name = info.Name,
                    content = $"// File is too large to display in editor ({info.Length / 1024:N0} KB)"
                });
                return;
            }

            // Retry up to 3 times with FileShare.ReadWrite to avoid locking collisions with active Qwen/MSBuild writes
            string? content = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    content = reader.ReadToEnd();
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(80);
                }
            }

            if (content == null) return; // File is still actively locked by writer; ignore until next flush

            PostToUi(new
            {
                type = "fileContent",
                path,
                name = info.Name,
                content
            });
        }
        catch (Exception ex)
        {
            // Transient preview failure should never kill agent state or hide the Stop button
            PostToUi(new { type = "fileError", message = $"Cannot preview {Path.GetFileName(path)}: {ex.Message}" });
        }
    }

    private void SaveFileToDisk(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            PostToUi(new { type = "fileSaved", path });
        }
        catch (Exception ex)
        {
            PostToUi(new { type = "error", message = $"Failed to save file: {ex.Message}" });
        }
    }

    private void SendChatSessions()
    {
        try
        {
            var workspaceChatsDir = FindWorkspaceChatsDirectory(_currentWorkspace);
            if (workspaceChatsDir == null || !Directory.Exists(workspaceChatsDir))
            {
                PostToUi(new { type = "chatSessions", currentSessionId = _currentSessionId, sessions = Array.Empty<object>() });
                return;
            }

            var chatFiles = new DirectoryInfo(workspaceChatsDir)
                .GetFiles("*.jsonl")
                .Where(f => f.Length > 0)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(40);

            var sessions = new List<object>();
            foreach (var file in chatFiles)
            {
                var sessionId = Path.GetFileNameWithoutExtension(file.Name);
                string title = "Chat Session";
                int messageCount = 0;

                // 1. Check if <sessionId>.title.json exists (official Qwen CLI title metadata)
                var titleJsonPath = Path.Combine(workspaceChatsDir, sessionId + ".title.json");
                if (File.Exists(titleJsonPath))
                {
                    try
                    {
                        var tNode = JsonNode.Parse(File.ReadAllText(titleJsonPath));
                        var disp = tNode?["displayName"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(disp)) title = disp;
                    }
                    catch { }
                }

                // 2. Count user messages and fall back to first user prompt if title was default
                try
                {
                    foreach (var line in File.ReadLines(file.FullName))
                    {
                        var node = JsonNode.Parse(line);
                        var type = node?["type"]?.GetValue<string>();
                        var subtype = node?["subtype"]?.GetValue<string>();
                        if (type == "user" && string.IsNullOrEmpty(subtype))
                        {
                            messageCount++;
                            if (title == "Chat Session")
                            {
                                var firstText = node?["message"]?["parts"]?[0]?["text"]?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(firstText))
                                {
                                    title = firstText.Length > 55 ? firstText[..55] + "…" : firstText;
                                }
                            }
                        }
                    }
                }
                catch { }

                sessions.Add(new
                {
                    sessionId,
                    title,
                    messageCount,
                    lastModified = file.LastWriteTime.ToString("MMM d, h:mm tt"),
                    file = file.FullName,
                    isActive = sessionId == _currentSessionId
                });
            }

            PostToUi(new { type = "chatSessions", currentSessionId = _currentSessionId, sessions });
        }
        catch { }
    }

    private static string? FindWorkspaceChatsDirectory(string workspacePath)
    {
        var qwenProjectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".qwen",
            "projects");

        if (!Directory.Exists(qwenProjectsRoot)) return null;

        var slug = EncodeQwenWorkspaceSlug(workspacePath);
        var exactDir = Path.Combine(qwenProjectsRoot, slug, "chats");
        if (Directory.Exists(exactDir)) return exactDir;

        var folderName = Path.GetFileName(workspacePath.TrimEnd('\\', '/')).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        var dirs = Directory.GetDirectories(qwenProjectsRoot);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (name.EndsWith("-" + folderName) || name == folderName)
            {
                var chats = Path.Combine(dir, "chats");
                if (Directory.Exists(chats)) return chats;
            }
        }
        return null;
    }

    private static string? FindSessionFile(string workspacePath, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var dir = FindWorkspaceChatsDirectory(workspacePath);
        if (dir == null) return null;
        var target = Path.Combine(dir, sessionId + ".jsonl");
        return File.Exists(target) ? target : null;
    }

    private static (string sessionId, string filePath)? FindMostRecentSession(string workspacePath)
    {
        var dir = FindWorkspaceChatsDirectory(workspacePath);
        if (dir == null || !Directory.Exists(dir)) return null;
        var mostRecent = new DirectoryInfo(dir)
            .GetFiles("*.jsonl")
            .Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (mostRecent == null) return null;
        return (Path.GetFileNameWithoutExtension(mostRecent.Name), mostRecent.FullName);
    }

    private static string EncodeQwenWorkspaceSlug(string workspacePath)
    {
        var normalized = workspacePath.TrimEnd('\\', '/').ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append('-');
        }
        return sb.ToString();
    }

    private void LoadSessionHistory(string sessionId, string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            StopActiveQwenProcess();
            _currentSessionId = sessionId;
            _settings.LastSessionId = sessionId;
            _settings.LastWorkspace = _currentWorkspace;
            SaveSettings(_settings);

            var messages = new List<object>();
            foreach (var line in File.ReadLines(filePath))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    var type = node?["type"]?.GetValue<string>();
                    var subtype = node?["subtype"]?.GetValue<string>();

                    if (type == "user" && string.IsNullOrEmpty(subtype))
                    {
                        var text = node?["message"]?["parts"]?[0]?["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            messages.Add(new { role = "user", text });
                        }
                    }
                    else if (type == "assistant")
                    {
                        var parts = node?["message"]?["parts"]?.AsArray();
                        if (parts != null)
                        {
                            string thinking = "";
                            string text = "";
                            var tools = new List<object>();

                            foreach (var p in parts)
                            {
                                if (p?["thought"]?.GetValue<bool>() == true)
                                {
                                    thinking += p?["text"]?.GetValue<string>() ?? "";
                                }
                                else if (p?["text"] != null)
                                {
                                    text += p["text"]!.GetValue<string>();
                                }
                                else if (p?["functionCall"] != null)
                                {
                                    tools.Add(new
                                    {
                                        name = p["functionCall"]!["name"]?.GetValue<string>(),
                                        args = p["functionCall"]!["args"]?.ToJsonString()
                                    });
                                }
                            }

                            messages.Add(new { role = "assistant", thinking, text, tools });
                        }
                    }
                }
                catch { }
            }

            PostToUi(new
            {
                type = "sessionLoaded",
                sessionId,
                messages
            });
        }
        catch (Exception ex)
        {
            PostToUi(new { type = "error", message = $"Failed to load session: {ex.Message}" });
        }
    }

    private async Task StartCompactionAsync(string triggerReason)
    {
        if (_isCompacting) return;
        _isCompacting = true;
        _userCancelled = false;

        PostToUi(new
        {
            type = "compactionStarted",
            trigger = triggerReason,
            cachedTokens = _lastKnownNPrompt
        });

        const string summaryPrompt =
            "Generate a concise, factual PROJECT_SUMMARY.md in the project root containing:\n" +
            "1. Current Architecture & Completed Features\n" +
            "2. Working File Tree & Tech Stack\n" +
            "3. Unfinished Items / Known Bugs\n" +
            "4. Exact Next Steps to Execute.\n" +
            "Keep it brief and markdown-formatted.";

        await RunQwenPromptAsync(summaryPrompt);
    }

    private async Task RunQwenPromptAsync(string prompt)
    {
        StopActiveQwenProcess();
        _userCancelled = false;
        SanitizeGlobalMemories();

        var activeProfile = _settings.Providers.FirstOrDefault(p => p.Id == _settings.ActiveProviderId)
                            ?? _settings.Providers.First();

        var runner = ResolveQwenRunner();
        if (runner.ExePath == null)
        {
            PostToUi(new { type = "agentError", message = "Could not find 'node.exe' or 'qwen.cmd' in PATH or npm global bin." });
            return;
        }

        var args = new List<string>(runner.PrefixArgs)
        {
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--approval-mode", "yolo",
            "--auth-type", activeProfile.AuthType,
            "--model", activeProfile.Model
        };

        string effectiveBaseUrl = activeProfile.BaseUrl;
        if (activeProfile.IsLocal && _settings.InFlightPruningEnabled)
        {
            effectiveBaseUrl = "http://127.0.0.1:8082/v1";
        }

        if (!string.IsNullOrWhiteSpace(effectiveBaseUrl))
        {
            args.Add("--openai-base-url");
            args.Add(effectiveBaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(activeProfile.ApiKey))
        {
            args.Add("--openai-api-key");
            args.Add(activeProfile.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(_currentSessionId))
        {
            args.Add("--resume");
            args.Add(_currentSessionId);
        }

        args.Add("-p");
        args.Add(prompt);

        var psi = new ProcessStartInfo
        {
            FileName = runner.ExePath,
            WorkingDirectory = _currentWorkspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["QWEN_STREAM_MAX_LIFETIME_MS"] = "0";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        if (!string.IsNullOrWhiteSpace(effectiveBaseUrl))
            psi.Environment["OPENAI_BASE_URL"] = effectiveBaseUrl;
        if (!string.IsNullOrWhiteSpace(activeProfile.ApiKey))
            psi.Environment["OPENAI_API_KEY"] = activeProfile.ApiKey;
        if (!string.IsNullOrWhiteSpace(activeProfile.Model))
            psi.Environment["OPENAI_MODEL"] = activeProfile.Model;
        if (activeProfile.AuthType == "gemini" && !string.IsNullOrWhiteSpace(activeProfile.ApiKey))
            psi.Environment["GEMINI_API_KEY"] = activeProfile.ApiKey;
        if (activeProfile.AuthType == "anthropic" && !string.IsNullOrWhiteSpace(activeProfile.ApiKey))
            psi.Environment["ANTHROPIC_API_KEY"] = activeProfile.ApiKey;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _activeQwenProcess = proc;

        PostToUi(new
        {
            type = "agentStarted",
            prompt,
            provider = activeProfile.Name,
            model = activeProfile.Model
        });

        try
        {
            proc.Start();

            var stdoutTask = Task.Run(async () =>
            {
                while (!proc.StandardOutput.EndOfStream)
                {
                    var line = await proc.StandardOutput.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    BeginInvoke(() => HandleQwenStreamLine(line));
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                while (!proc.StandardError.EndOfStream)
                {
                    var errLine = await proc.StandardError.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(errLine)) continue;

                    BeginInvoke(() => PostToUi(new { type = "qwenStderr", line = errLine }));
                }
            });

            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync();

            BeginInvoke(async () =>
            {
                PostToUi(new { type = "agentCompleted", exitCode = proc.ExitCode, sessionId = _currentSessionId });
                SendFileTree();
                SendChatSessions();

                if (_isCompacting)
                {
                    if (proc.ExitCode == 0 && !_userCancelled)
                    {
                        _isCompacting = false;
                        var oldTokens = _lastKnownNPrompt;
                        _lastKnownNPrompt = 0;

                        // Clear current session to force a fresh session resumption
                        _currentSessionId = null;
                        _settings.LastSessionId = null;
                        SaveSettings(_settings);

                        PostToUi(new { type = "chatCleared", sessionId = (string?)null });
                        PostToUi(new
                        {
                            type = "compactionPhase2",
                            cachedTokens = oldTokens,
                            message = "Context cleared. Resuming project from PROJECT_SUMMARY.md in fresh session..."
                        });
                        SendChatSessions();

                        await Task.Delay(500);

                        const string resumePrompt =
                            "Resuming project from PROJECT_SUMMARY.md. Review the summary and files on disk, then proceed with the next unfinished step.";

                        _ = RunQwenPromptAsync(resumePrompt);
                    }
                    else
                    {
                        _isCompacting = false;
                        PostToUi(new { type = "compactionAborted", message = "Compaction was cancelled or did not exit cleanly." });
                    }
                }
                else if (!_userCancelled && _settings.AutoCompact && _lastKnownNPrompt >= _settings.AutoCompactThreshold && !string.IsNullOrWhiteSpace(_currentSessionId))
                {
                    PostToUi(new
                    {
                        type = "autoCompactTriggered",
                        nPrompt = _lastKnownNPrompt,
                        threshold = _settings.AutoCompactThreshold
                    });

                    await Task.Delay(500);
                    _ = StartCompactionAsync("auto");
                }
            });
        }
        catch (Exception ex)
        {
            BeginInvoke(() => PostToUi(new { type = "agentError", message = $"Agent execution error: {ex.Message}" }));
        }
    }

    private void HandleQwenStreamLine(string rawJsonLine)
    {
        try
        {
            var node = JsonNode.Parse(rawJsonLine);
            if (node == null) return;

            var sessionId = node["session_id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sessionId) && _currentSessionId != sessionId)
            {
                _currentSessionId = sessionId;
                _settings.LastSessionId = sessionId;
                _settings.LastWorkspace = _currentWorkspace;
                SaveSettings(_settings);
                BeginInvoke(SendChatSessions);
            }

            PostToUi(new
            {
                type = "qwenStream",
                raw = rawJsonLine,
                packet = node
            });
        }
        catch
        {
            PostToUi(new { type = "qwenStderr", line = rawJsonLine });
        }
    }

    private async Task RunNativePromptAsync(string prompt)
    {
        StopActiveQwenProcess();
        _userCancelled = false;
        SanitizeGlobalMemories();

        var activeProfile = _settings.Providers.FirstOrDefault(p => p.Id == _settings.ActiveProviderId)
                            ?? _settings.Providers.First();

        await _nativeAgentEngine.RunPromptAsync(_currentWorkspace, prompt, activeProfile, _settings);
    }

    private void StopActiveQwenProcess()
    {
        _nativeAgentEngine?.Stop();
        var proc = _activeQwenProcess;
        _activeQwenProcess = null;
        if (proc == null) return;

        _userCancelled = true;
        _isCompacting = false;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            proc.Dispose();
        }
    }

    private static (string? ExePath, List<string> PrefixArgs) ResolveQwenRunner()
    {
        var appDataNpmCli = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "node_modules",
            "@qwen-code",
            "qwen-code",
            "cli-entry.js");

        var nodeExe = @"C:\Program Files\nodejs\node.exe";
        if (!File.Exists(nodeExe))
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (var dir in pathDirs)
            {
                var candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate))
                {
                    nodeExe = candidate;
                    break;
                }
            }
        }

        // Bypassing cmd.exe / qwen.cmd directly preserves multi-line prompts, '<', and quotes without shell mangling
        if (File.Exists(appDataNpmCli) && File.Exists(nodeExe))
        {
            return (nodeExe, new List<string> { appDataNpmCli });
        }

        var appDataNpm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "qwen.cmd");
        if (File.Exists(appDataNpm)) return (appDataNpm, new List<string>());

        var envDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in envDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, "qwen.cmd");
                if (File.Exists(candidate)) return (candidate, new List<string>());
            }
            catch { }
        }
        return (null, new List<string>());
    }

    private async Task PollLlamaServerAsync()
    {
        if (_isPollingTelemetry) return;
        // When active generation is running, throttle polling to reduce server mutex contention
        if (_nativeAgentEngine?.IsRunning == true)
        {
            if (Random.Shared.Next(0, 3) != 0) return;
        }
        _isPollingTelemetry = true;

        try
        {
            var activeProfile = _settings.Providers.FirstOrDefault(p => p.Id == _settings.ActiveProviderId)
                                ?? _settings.Providers.First();
            var targetModel = activeProfile.IsLocal && !string.IsNullOrWhiteSpace(activeProfile.Model)
                ? activeProfile.Model
                : "qwen3.8-27b-q4";

            // Check /v1/models first to support Router Mode without blocking on unloaded models
            var modelsResp = await _httpClient.GetStringAsync("http://127.0.0.1:8081/v1/models");
            var modelsNode = JsonNode.Parse(modelsResp);
            var dataArr = modelsNode?["data"]?.AsArray();

            if (dataArr != null)
            {
                var matchingModel = dataArr.FirstOrDefault(m =>
                    string.Equals(m?["id"]?.GetValue<string>(), targetModel, StringComparison.OrdinalIgnoreCase));
                var statusVal = matchingModel?["status"]?["value"]?.GetValue<string>();

                if (statusVal == "unloaded")
                {
                    if (_loadingModels.Add(targetModel))
                    {
                        var modelToLoad = targetModel;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _modelLoaderClient.GetStringAsync(
                                    $"http://127.0.0.1:8081/slots?model={Uri.EscapeDataString(modelToLoad)}");
                            }
                            catch { }
                            finally
                            {
                                BeginInvoke(() => _loadingModels.Remove(modelToLoad));
                            }
                        });
                    }

                    PostToUi(new
                    {
                        type = "telemetry",
                        online = true,
                        modelStatus = "loading",
                        activeModel = targetModel,
                        isProcessing = false,
                        nPrompt = 0,
                        nPromptProcessed = 0,
                        nDecoded = 0,
                        nCtx = 0,
                        tps = 0.0
                    });
                    return;
                }

                if (statusVal == "loading")
                {
                    PostToUi(new
                    {
                        type = "telemetry",
                        online = true,
                        modelStatus = "loading",
                        activeModel = targetModel,
                        isProcessing = false,
                        nPrompt = 0,
                        nPromptProcessed = 0,
                        nDecoded = 0,
                        nCtx = 0,
                        tps = 0.0
                    });
                    return;
                }
            }

            var resp = await _httpClient.GetStringAsync(
                $"http://127.0.0.1:8081/slots?model={Uri.EscapeDataString(targetModel)}");
            var arr = JsonNode.Parse(resp)?.AsArray();
            if (arr == null || arr.Count == 0) return;

            var slot = arr[0]!;
            bool isProcessing = slot["is_processing"]?.GetValue<bool>() ?? false;
            int nPrompt = slot["n_prompt_tokens"]?.GetValue<int>() ?? 0;
            int nPromptProcessed = slot["n_prompt_tokens_processed"]?.GetValue<int>() ?? 0;
            int nCtx = slot["n_ctx"]?.GetValue<int>() ?? 65536;
            int nDecoded = slot["next_token"]?[0]?["n_decoded"]?.GetValue<int>() ?? 0;

            _lastKnownNPrompt = nPrompt;
            _lastKnownNCtx = nCtx;

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastTelemetryTime).TotalSeconds;
            if (isProcessing && nDecoded > _lastDecodedTokens && elapsed > 0.2)
            {
                var instantTps = (nDecoded - _lastDecodedTokens) / elapsed;
                _smoothedTps = _smoothedTps <= 0.1 ? instantTps : (_smoothedTps * 0.6 + instantTps * 0.4);
            }
            else if (!isProcessing)
            {
                _smoothedTps = 0;
            }

            _lastDecodedTokens = nDecoded;
            _lastTelemetryTime = now;

            PostToUi(new
            {
                type = "telemetry",
                online = true,
                modelStatus = "loaded",
                activeModel = targetModel,
                isProcessing,
                nPrompt,
                nPromptProcessed,
                nDecoded,
                nCtx,
                tps = Math.Round(_smoothedTps, 1)
            });
        }
        catch
        {
            PostToUi(new
            {
                type = "telemetry",
                online = false,
                modelStatus = "offline",
                isProcessing = false,
                nPrompt = 0,
                nPromptProcessed = 0,
                nDecoded = 0,
                nCtx = 0,
                tps = 0.0
            });
        }
        finally
        {
            _isPollingTelemetry = false;
        }
    }

    private void PostToUi(object payload)
    {
        if (IsDisposed) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsDisposed && _webView?.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.PostWebMessageAsJson(json);
                        }
                    }
                    catch { }
                }));
            }
            else
            {
                if (_webView?.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
            }
        }
        catch { }
    }

    private static StudioSettings LoadSettings()
    {
        var defaults = StudioSettings.CreateDefault();
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var raw = File.ReadAllText(SettingsFilePath);
                var parsed = JsonSerializer.Deserialize<StudioSettings>(raw);
                if (parsed != null && parsed.Providers.Count > 0)
                {
                    if (parsed.AutoCompactThreshold <= 0)
                    {
                        parsed.AutoCompactThreshold = 65000;
                    }
                    if (parsed.InFlightPruningThreshold <= 0)
                    {
                        parsed.InFlightPruningThreshold = 40000;
                    }

                    // Auto-merge any newly added default profiles (like 9B & 14B Turbo) into existing user settings
                    bool changed = false;
                    foreach (var def in defaults.Providers)
                    {
                        if (!parsed.Providers.Any(p => p.Id == def.Id))
                        {
                            // Insert local profiles near the top so they group together cleanly
                            if (def.IsLocal)
                                parsed.Providers.Insert(Math.Min(2, parsed.Providers.Count), def);
                            else
                                parsed.Providers.Add(def);
                            changed = true;
                        }
                    }
                    if (changed) SaveSettings(parsed);
                    return parsed;
                }
            }
        }
        catch { }

        return defaults;
    }

    private static void SaveSettings(StudioSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }
}

public sealed class StudioSettings
{
    public string AgentEngine { get; set; } = "qwen-cli";
    public string TestMode { get; set; } = "fast"; // "none" | "fast" | "rigorous"
    public string ActiveProviderId { get; set; } = "local-qwen38-q4";
    public string? LastWorkspace { get; set; } = @"C:\Users\Bart\Projects\webcontacts";
    public string? LastSessionId { get; set; }
    public bool AutoCompact { get; set; } = true;
    public int AutoCompactThreshold { get; set; } = 65000;
    public bool InFlightPruningEnabled { get; set; } = true;
    public int InFlightPruningThreshold { get; set; } = 40000;
    public List<ProviderProfile> Providers { get; set; } = new();

    public static StudioSettings CreateDefault() => new()
    {
        AgentEngine = "qwen-cli",
        TestMode = "fast",
        ActiveProviderId = "local-qwen38-q4",
        AutoCompact = true,
        AutoCompactThreshold = 65000,
        InFlightPruningEnabled = true,
        InFlightPruningThreshold = 40000,
        Providers = new List<ProviderProfile>
        {
            new()
            {
                Id = "local-qwen38-q4",
                Name = "Local: Qwen 3.8 27B Architect (UD-Q4_K_XL + 0.8B Draft)",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen3.8-27b-q4",
                IsLocal = true
            },
            new()
            {
                Id = "local-qwen35-9b-turbo",
                Name = "Local: Qwen 3.5 9B Turbo (UD-Q6_K_XL + 0.8B Draft)",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen3.5-9b-turbo",
                IsLocal = true
            },
            new()
            {
                Id = "local-qwen35-9b-q4",
                Name = "Local: Qwen 3.5 9B Fast (Q4_K_M + 0.8B Draft)",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen3.5-9b-q4",
                IsLocal = true
            },
            new()
            {
                Id = "local-qwen3-14b-turbo",
                Name = "Local: Qwen 3 14B Turbo (Q4_K_M + 0.6B Draft)",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen3-14b-turbo",
                IsLocal = true
            },
            new()
            {
                Id = "local-qwen38-q6",
                Name = "Local: Qwen 3.8 27B Dense (UD-Q6_K_XL)",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen3.8-27b",
                IsLocal = true
            },
            new()
            {
                Id = "local-qwen25-32b",
                Name = "Local: Qwen 2.5 Coder 32B Dense",
                AuthType = "openai",
                BaseUrl = "http://127.0.0.1:8081/v1",
                ApiKey = "local-no-key-needed",
                Model = "qwen2.5-coder-32b",
                IsLocal = true
            },
            new()
            {
                Id = "cloud-gemini",
                Name = "Cloud: Google Gemini 2.5 Pro",
                AuthType = "gemini",
                BaseUrl = "",
                ApiKey = "",
                Model = "gemini-2.5-pro",
                IsLocal = false
            },
            new()
            {
                Id = "cloud-deepseek",
                Name = "Cloud: DeepSeek V3 / R1 (OpenAI API)",
                AuthType = "openai",
                BaseUrl = "https://api.deepseek.com/v1",
                ApiKey = "",
                Model = "deepseek-chat",
                IsLocal = false
            },
            new()
            {
                Id = "cloud-openrouter",
                Name = "Cloud: OpenRouter / Big-Pickle / Custom",
                AuthType = "openai",
                BaseUrl = "https://openrouter.ai/api/v1",
                ApiKey = "",
                Model = "qwen/qwen3-coder",
                IsLocal = false
            }
        }
    };
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string AuthType { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public bool IsLocal { get; set; }
}

