using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

public sealed class CodeListSymbolsTool : ITool
{
    private const int DefaultMaxSymbols = 2000;
    private const int HardMaxSymbols = 5000;

    private readonly SymbolService _symbols;

    public CodeListSymbolsTool(SymbolService symbols)
    {
        _symbols = symbols;
    }

    public string Name => "code_list_symbols";

    public string Description => "Extract code structure (namespaces, classes, methods, functions) from a single source file. Cheaper than read_file for orientation: returns ~50–200 tokens instead of hundreds. To orient in an unfamiliar codebase, first discover relevant files by name (list_files / file search) so you target the source that matters, then outline them one at a time. To read one symbol's source rather than the whole file, use code_extract_symbol; to search code by symbol, look up code_find_definitions / code_find_calls / code_search with lookup_tool and invoke them via call_tool.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":        { "type": "string",  "description": "Source file path (workspace-relative or absolute)." },
        "max_symbols": { "type": "integer", "description": "Cap on symbols emitted. Default 2000, hard cap 5000.", "minimum": 1, "maximum": 5000 }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);

        var maxSymbols = DefaultMaxSymbols;
        if (arguments.TryGetProperty("max_symbols", out var msProp) && msProp.ValueKind == JsonValueKind.Number)
            maxSymbols = Math.Clamp(msProp.GetInt32(), 1, HardMaxSymbols);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (Directory.Exists(resolved))
            return ToolResult.Error("code_list_symbols operates on a single file. Pass a source file path; use list_files or file search to discover the files that matter first, then outline them one at a time.");

        if (!File.Exists(resolved))
            return ToolResult.Error($"Path not found: {resolved}");

        return await OutlineSingleFileAsync(resolved, Path.GetFileName(resolved), maxSymbols, cancellationToken);
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
}
