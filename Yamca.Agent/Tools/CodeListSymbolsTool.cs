using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

public sealed class CodeListSymbolsTool : ITool
{
    private const int DefaultMaxFiles = 50;
    private const int HardMaxFiles = 200;
    private const int DefaultMaxSymbols = 2000;
    private const int HardMaxSymbols = 5000;

    private readonly SymbolService _symbols;

    public CodeListSymbolsTool(SymbolService symbols)
    {
        _symbols = symbols;
    }

    public string Name => "code_list_symbols";

    public string Description => "Extract code structure (namespaces, classes, methods, functions) from a source file or directory. Cheaper than read_file for orientation: returns ~50–200 tokens per file instead of hundreds. Honors .gitignore for directories; unsupported extensions are skipped (directory mode) or reported (file mode). To read one symbol's source rather than the whole file, use code_extract_symbol; to search code by symbol, search for code_find_definitions / code_find_calls / code_search via load_tool.";

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
            : await OutlineSingleFileAsync(resolved, Path.GetFileName(resolved), maxSymbols, cancellationToken);
    }

    private async Task<ToolResult> OutlineSingleFileAsync(string absolutePath, string displayPath, int maxSymbols, CancellationToken ct)
    {
        var load = await _symbols.LoadAsync(absolutePath, ct);
        return load.Status switch
        {
            SymbolLoadStatus.Unsupported => ToolResult.Error(load.Note ?? "Unsupported file type."),
            SymbolLoadStatus.NoExtractor => ToolResult.Error(load.Note ?? "No symbol extractor."),
            SymbolLoadStatus.ReadError => ToolResult.Error($"Could not render symbols for '{absolutePath}'."),
            SymbolLoadStatus.Skipped => ToolResult.Ok(RenderHeaderOnly(displayPath, load.Note ?? "# skipped")),
            _ => ToolResult.Ok(RenderBlock(displayPath, Cap(load.Symbols, maxSymbols), load.HasErrors)),
        };
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

                var load = await _symbols.LoadAsync(file, ct);
                // Files we can't outline (wrong extension, no extractor, unreadable) are
                // silently skipped in directory mode.
                if (load.Status is SymbolLoadStatus.Unsupported or SymbolLoadStatus.NoExtractor or SymbolLoadStatus.ReadError)
                    continue;

                var rel = FileSearch.ToForwardSlashRelative(root, file);

                string rendered;
                if (load.Status == SymbolLoadStatus.Skipped)
                {
                    rendered = RenderHeaderOnly(rel, load.Note ?? "# skipped");
                }
                else
                {
                    var remaining = Math.Max(0, maxSymbols - symbolsEmitted);
                    if (remaining == 0) { truncatedSymbols = true; break; }
                    rendered = RenderBlock(rel, Cap(load.Symbols, remaining), load.HasErrors);
                }

                // Lines that are not the path header (line 0 of the block).
                var symbolLineCount = CountLines(rendered) - 1;
                if (symbolLineCount <= 0) continue;

                output.Append(rendered);
                filesEmitted++;
                symbolsEmitted += symbolLineCount;
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("code_list_symbols cancelled.");
        }

        if (filesEmitted == 0)
            return ToolResult.Ok("(no supported source files found)");

        if (truncatedFiles)
            output.Append("…[truncated at ").Append(maxFiles).Append(" files]\n");
        else if (truncatedSymbols)
            output.Append("…[truncated at ").Append(maxSymbols).Append(" symbols]\n");

        return ToolResult.Ok(output.ToString());
    }

    private static IReadOnlyList<Symbol> Cap(IReadOnlyList<Symbol> symbols, int max) =>
        symbols.Count <= max ? symbols : symbols.Take(max).ToList();

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
