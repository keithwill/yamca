using System.Text;
using System.Text.Json;
using TreeSitter;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

public sealed class ListSymbolsTool : ITool
{
    private const int DefaultMaxFiles = 50;
    private const int HardMaxFiles = 200;
    private const int DefaultMaxSymbols = 2000;
    private const int HardMaxSymbols = 5000;
    private const long MaxFileBytes = 2L * 1024 * 1024;

    private readonly ParsedTreeCache _cache;
    private readonly Dictionary<string, ISymbolExtractor> _extractorsByLanguage;

    public ListSymbolsTool(ParsedTreeCache cache, IEnumerable<ISymbolExtractor> extractors)
    {
        _cache = cache;
        _extractorsByLanguage = extractors.ToDictionary(e => e.LanguageId, StringComparer.Ordinal);
    }

    public string Name => "list_symbols";

    public string Description => "Extract code structure (namespaces, classes, methods, functions) from a source file or directory. Cheaper than read_file for orientation: returns ~50–200 tokens per file instead of hundreds. Honors .gitignore for directories; files with unsupported extensions are silently skipped (directory mode) or reported (file mode).";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":        { "type": "string",  "description": "File or directory path (workspace-relative or absolute)." },
        "max_files":   { "type": "integer", "description": "Directory mode: cap on files outlined. Default 50, hard cap 200.", "minimum": 1, "maximum": 200 },
        "max_symbols": { "type": "integer", "description": "Global cap on symbols emitted across all files. Default 2000, hard cap 5000.", "minimum": 1, "maximum": 5000 }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);

        var maxFiles = DefaultMaxFiles;
        if (arguments.TryGetProperty("max_files", out var mfProp) && mfProp.ValueKind == JsonValueKind.Number)
            maxFiles = Math.Clamp(mfProp.GetInt32(), 1, HardMaxFiles);

        var maxSymbols = DefaultMaxSymbols;
        if (arguments.TryGetProperty("max_symbols", out var msProp) && msProp.ValueKind == JsonValueKind.Number)
            maxSymbols = Math.Clamp(msProp.GetInt32(), 1, HardMaxSymbols);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        var isDirectory = Directory.Exists(resolved);
        if (!isDirectory && !File.Exists(resolved))
            return ToolResult.Error($"Path not found: {resolved}");

        return isDirectory
            ? await OutlineDirectoryAsync(resolved, maxFiles, maxSymbols, cancellationToken)
            : await OutlineSingleFileAsync(resolved, displayPath: Path.GetFileName(resolved), maxSymbols, cancellationToken);
    }

    private async Task<ToolResult> OutlineSingleFileAsync(string absolutePath, string displayPath, int maxSymbols, CancellationToken ct)
    {
        var languageId = LanguageRouter.GetLanguageId(absolutePath);
        if (languageId is null)
            return ToolResult.Error($"Unsupported file type: {Path.GetExtension(absolutePath)}");

        if (!_extractorsByLanguage.TryGetValue(languageId, out var extractor))
            return ToolResult.Error($"No symbol extractor registered for language '{languageId}'.");

        var rendered = await RenderFileAsync(absolutePath, displayPath, extractor, maxSymbols, ct);
        return rendered is null
            ? ToolResult.Error($"Could not render symbols for '{absolutePath}'.")
            : ToolResult.Ok(rendered);
    }

    private async Task<ToolResult> OutlineDirectoryAsync(string root, int maxFiles, int maxSymbols, CancellationToken ct)
    {
        var output = new StringBuilder();
        var filesEmitted = 0;
        var symbolsEmitted = 0;
        var truncatedFiles = false;
        var truncatedSymbols = false;

        try
        {
            foreach (var file in FileSearch.Enumerate(root, "**/*", respectGitignore: true, ct))
            {
                if (filesEmitted >= maxFiles) { truncatedFiles = true; break; }
                if (symbolsEmitted >= maxSymbols) { truncatedSymbols = true; break; }

                var languageId = LanguageRouter.GetLanguageId(file);
                if (languageId is null) continue;
                if (!_extractorsByLanguage.TryGetValue(languageId, out var extractor)) continue;

                var rel = FileSearch.ToForwardSlashRelative(root, file);
                var remaining = Math.Max(0, maxSymbols - symbolsEmitted);
                if (remaining == 0) { truncatedSymbols = true; break; }

                var rendered = await RenderFileAsync(file, rel, extractor, remaining, ct);
                if (rendered is null) continue;

                // CountSymbolLines: lines that are not the path header (line 0 of the block).
                var symbolLineCount = CountLines(rendered) - 1;
                if (symbolLineCount <= 0) continue;

                output.Append(rendered);
                filesEmitted++;
                symbolsEmitted += symbolLineCount;
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("list_symbols cancelled.");
        }

        if (filesEmitted == 0)
            return ToolResult.Ok("(no supported source files found)");

        if (truncatedFiles)
            output.Append("…[truncated at ").Append(maxFiles).Append(" files]\n");
        else if (truncatedSymbols)
            output.Append("…[truncated at ").Append(maxSymbols).Append(" symbols]\n");

        return ToolResult.Ok(output.ToString());
    }

    private async Task<string?> RenderFileAsync(string absolutePath, string displayPath, ISymbolExtractor extractor, int maxSymbols, CancellationToken ct)
    {
        FileInfo info;
        try { info = new FileInfo(absolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (info.Length > MaxFileBytes)
            return RenderHeaderOnly(displayPath, $"# skipped: too large ({info.Length / 1024} KB)");

        if (await FileProbe.IsLikelyBinaryAsync(absolutePath, ct))
            return RenderHeaderOnly(displayPath, "# skipped: binary file");

        var mtime = info.LastWriteTimeUtc;
        if (_cache.TryGet(absolutePath, mtime, info.Length, out var cached))
            return cached;

        string source;
        try
        {
            source = await File.ReadAllTextAsync(absolutePath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string rendered;
        try
        {
            using var language = new Language(extractor.LanguageId);
            using var parser = new Parser(language);
            using var tree = parser.Parse(source);
            if (tree is null)
                return RenderHeaderOnly(displayPath, "# parse failed");

            var symbols = extractor.Extract(tree.RootNode, source).Take(maxSymbols).ToList();
            rendered = RenderBlock(displayPath, symbols, tree.RootNode.HasError);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or ArgumentException)
        {
            // Native lib for this language failed to load, or query failed. Surface a
            // skipped-block so the caller still gets a file entry.
            return RenderHeaderOnly(displayPath, $"# skipped: {ex.Message}");
        }

        _cache.Set(absolutePath, mtime, info.Length, rendered);
        return rendered;
    }

    private static string RenderHeaderOnly(string displayPath, string note)
    {
        var sb = new StringBuilder();
        sb.Append(displayPath).Append('\n');
        sb.Append("  ").Append(note).Append('\n');
        return sb.ToString();
    }

    private static string RenderBlock(string displayPath, IReadOnlyList<Symbol> symbols, bool hasErrors)
    {
        var sb = new StringBuilder();
        sb.Append(displayPath).Append('\n');
        if (hasErrors) sb.Append("  # warning: partial parse\n");
        if (symbols.Count == 0 && !hasErrors)
            sb.Append("  (no symbols)\n");

        foreach (var s in symbols)
        {
            for (var i = 0; i < s.Depth; i++) sb.Append("  ");
            sb.Append(s.Display).Append("  [L").Append(s.Line).Append("]\n");
        }
        return sb.ToString();
    }

    private static int CountLines(string s)
    {
        var n = 0;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }
}
