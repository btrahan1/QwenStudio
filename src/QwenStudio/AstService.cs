using System.Text;
using System.Text.RegularExpressions;

namespace QwenStudio;

public sealed class AstSymbol
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // "class", "method", "interface", "function", "property"
    public int LineNumber { get; set; }
    public string Signature { get; set; } = string.Empty;
}

public sealed class AstService
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".smoketest", "dist", "build", ".qwen"
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime LastModifiedUtc, string Skeleton)> _fileAstCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Generates a clean, compact code skeleton (AST outline) for a single file,
    /// preserving all class, method, and function signatures while stripping implementation bodies.
    /// </summary>
    public string ExtractFileSkeleton(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => ExtractCSharpSkeleton(content),
            ".js" or ".jsx" or ".ts" or ".tsx" => ExtractJavaScriptSkeleton(content),
            _ => ExtractGenericSkeleton(content)
        };
    }

    /// <summary>
    /// Scans a workspace directory and builds a comprehensive, token-efficient
    /// architectural map of the entire project (classes, methods, interfaces, exports).
    /// Typically under 1,000–1,500 tokens even for large repositories.
    /// </summary>
    public string GenerateWorkspaceRepoMap(string workspaceRoot, int maxFiles = 30)
    {
        if (!Directory.Exists(workspaceRoot)) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# Architectural Repo Map (AST Skeletons)");
        sb.AppendLine("// Implementation bodies collapsed to conserve context. Full files remain on disk.\n");

        var files = Directory.EnumerateFiles(workspaceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => !IsIgnored(f))
            .Where(f => IsCodeFile(f))
            .Take(maxFiles)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var relPath = Path.GetRelativePath(workspaceRoot, file);
                var lastWrite = File.GetLastWriteTimeUtc(file);

                if (!_fileAstCache.TryGetValue(file, out var cached) || cached.LastModifiedUtc != lastWrite)
                {
                    var content = File.ReadAllText(file);
                    var skeleton = ExtractFileSkeleton(file, content);
                    cached = (lastWrite, skeleton);
                    _fileAstCache[file] = cached;
                }

                if (!string.IsNullOrWhiteSpace(cached.Skeleton))
                {
                    sb.AppendLine($"--- {relPath} ---");
                    sb.AppendLine(cached.Skeleton.Trim());
                    sb.AppendLine();
                }
            }
            catch { }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts a list of navigable symbols (for editor outline or quick-jump).
    /// </summary>
    public List<AstSymbol> ExtractSymbols(string filePath, string content)
    {
        var symbols = new List<AstSymbol>();
        var lines = content.Split('\n');
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*"))
                continue;

            if (ext == ".cs")
            {
                if (Regex.IsMatch(line, @"^(public|internal|private|protected)?\s*(sealed|abstract|static)?\s*(class|interface|record|enum|struct)\s+(\w+)"))
                {
                    var match = Regex.Match(line, @"(class|interface|record|enum|struct)\s+(\w+)");
                    if (match.Success)
                    {
                        symbols.Add(new AstSymbol
                        {
                            Kind = match.Groups[1].Value,
                            Name = match.Groups[2].Value,
                            LineNumber = i + 1,
                            Signature = line.TrimEnd('{').Trim()
                        });
                    }
                }
                else if (Regex.IsMatch(line, @"^(public|internal|private|protected)?\s*(override|virtual|async|static|sealed)*\s*[\w\<\>\[\],\s]+\s+(\w+)\s*\("))
                {
                    var match = Regex.Match(line, @"(\w+)\s*\(");
                    if (match.Success && !line.StartsWith("if") && !line.StartsWith("while") && !line.StartsWith("for") && !line.StartsWith("switch"))
                    {
                        symbols.Add(new AstSymbol
                        {
                            Kind = "method",
                            Name = match.Groups[1].Value,
                            LineNumber = i + 1,
                            Signature = line.TrimEnd('{').Trim()
                        });
                    }
                }
            }
            else if (ext is ".js" or ".ts" or ".jsx" or ".tsx")
            {
                if (Regex.IsMatch(line, @"^(export\s+)?(default\s+)?(class|function)\s+(\w+)"))
                {
                    var match = Regex.Match(line, @"(class|function)\s+(\w+)");
                    if (match.Success)
                    {
                        symbols.Add(new AstSymbol
                        {
                            Kind = match.Groups[1].Value,
                            Name = match.Groups[2].Value,
                            LineNumber = i + 1,
                            Signature = line.TrimEnd('{').Trim()
                        });
                    }
                }
                else if (Regex.IsMatch(line, @"^(export\s+)?(const|let|var)\s+(\w+)\s*=\s*(\(|async)"))
                {
                    var match = Regex.Match(line, @"(const|let|var)\s+(\w+)");
                    if (match.Success)
                    {
                        symbols.Add(new AstSymbol
                        {
                            Kind = "function",
                            Name = match.Groups[2].Value,
                            LineNumber = i + 1,
                            Signature = line.TrimEnd('{').Trim()
                        });
                    }
                }
            }
        }

        return symbols;
    }

    private static string ExtractCSharpSkeleton(string code)
    {
        var sb = new StringBuilder();
        var lines = code.Split('\n');
        int indent = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Namespaces, classes, interfaces, records, enums
            if (Regex.IsMatch(line, @"^(namespace|public|internal|private|protected)?\s*(sealed|abstract|static)?\s*(class|interface|record|enum|struct)\b"))
            {
                sb.AppendLine($"{new string(' ', indent * 2)}{line.TrimEnd('{').Trim()} {{");
                indent++;
            }
            // Method signatures
            else if (Regex.IsMatch(line, @"^(public|internal|private|protected)\s+(override|virtual|async|static|sealed)*\s*[\w\<\>\[\],\s]+\s+\w+\s*\(.*?\)") &&
                     !line.StartsWith("if") && !line.StartsWith("for") && !line.StartsWith("while"))
            {
                sb.AppendLine($"{new string(' ', indent * 2)}{line.TrimEnd('{').Trim()} {{ ... }}");
            }
            // Public properties
            else if (Regex.IsMatch(line, @"^(public|internal)\s+[\w\<\>\[\]\?]+\s+\w+\s*\{\s*get;"))
            {
                sb.AppendLine($"{new string(' ', indent * 2)}{line}");
            }
            else if (line.StartsWith("}"))
            {
                if (indent > 0) indent--;
                sb.AppendLine($"{new string(' ', indent * 2)}}}");
            }
        }

        return sb.Length > 0 ? sb.ToString() : ExtractGenericSkeleton(code);
    }

    private static string ExtractJavaScriptSkeleton(string code)
    {
        var sb = new StringBuilder();
        var lines = code.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (Regex.IsMatch(line, @"^(export\s+)?(default\s+)?(class|function)\s+\w+"))
            {
                sb.AppendLine(line.TrimEnd('{').Trim() + " { ... }");
            }
            else if (Regex.IsMatch(line, @"^(export\s+)?(const|let|var)\s+\w+\s*=\s*(async\s*)?\(.*?\)\s*=>"))
            {
                var match = Regex.Match(line, @"^(export\s+)?(const|let|var)\s+\w+\s*=\s*(async\s*)?\(.*?\)");
                sb.AppendLine((match.Success ? match.Value : line) + " => { ... }");
            }
            else if (Regex.IsMatch(line, @"^\w+\s*\(.*?\)\s*\{") && !line.StartsWith("if") && !line.StartsWith("for") && !line.StartsWith("catch"))
            {
                sb.AppendLine("  " + line.TrimEnd('{').Trim() + " { ... }");
            }
        }

        return sb.Length > 0 ? sb.ToString() : ExtractGenericSkeleton(code);
    }

    private static string ExtractGenericSkeleton(string code)
    {
        var lines = code.Split('\n');
        if (lines.Length <= 40) return code;

        // Keep first 20 and last 10 lines
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.AppendLine(lines[i]);
        sb.AppendLine($"\n// ... [{lines.Length - 30} lines collapsed] ...\n");
        for (int i = lines.Length - 10; i < lines.Length; i++) sb.AppendLine(lines[i]);
        return sb.ToString();
    }

    private static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".js" or ".ts" or ".jsx" or ".tsx" or ".py" or ".html" or ".css";
    }

    private static bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => IgnoredDirs.Contains(p));
    }
}

