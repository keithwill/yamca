using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

public sealed class CodeExtractSymbolTool : ITool
{
    private const int MaxCandidates = 25;
    private const int MaxHintNames = 30;

    private readonly SymbolService _symbols;

    public CodeExtractSymbolTool(SymbolService symbols)
    {
        _symbols = symbols;
    }

    public string Name => "code_extract_symbol";

    public string Description => "Return the source of a single named function, method, class, or other symbol from a file — far cheaper than read_file when you only need one declaration. Call code_list_symbols first to see available names. 'name' may be a leaf (Bar) or qualified (Foo.Bar); ambiguous leaf names return the candidate list so you can qualify.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Source file path (workspace-relative or absolute)." },
        "name": { "type": "string", "description": "Symbol name. Leaf ('Bar') or qualified ('Foo.Bar')." }
      },
      "required": ["path", "name"],
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
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var nameError))
            return ToolResult.Error(nameError);
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("'name' must not be empty.");

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);
        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        var load = await _symbols.LoadAsync(resolved, cancellationToken);
        if (!load.IsOk)
            return ToolResult.Error(load.Note ?? $"Could not read symbols from '{resolved}'.");

        var fileName = Path.GetFileName(resolved);
        var qualified = SymbolLookup.Qualify(load.Symbols);
        var matches = qualified.Where(qs => SymbolLookup.Matches(qs, name)).ToList();

        if (matches.Count == 0)
            return ToolResult.Error(NoMatchMessage(name, fileName, qualified));

        if (matches.Count > 1)
            return ToolResult.Ok(CandidateList(name, matches));

        var hit = matches[0];
        var slice = SymbolLookup.SliceLines(load.Source, hit.Symbol.Line, hit.Symbol.EndLine);
        var sb = new StringBuilder();
        sb.Append(hit.QualifiedName).Append("  [").Append(fileName).Append(':')
          .Append(hit.Symbol.Line).Append('-').Append(hit.Symbol.EndLine).Append("]\n");
        sb.Append(slice).Append('\n');
        return ToolResult.Ok(sb.ToString());
    }

    private static string CandidateList(string name, List<QualifiedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple symbols match '").Append(name).Append("'. Re-run with a qualified name:\n");
        foreach (var qs in matches.Take(MaxCandidates))
            sb.Append("  ").Append(qs.QualifiedName).Append("  [L").Append(qs.Symbol.Line).Append("]\n");
        if (matches.Count > MaxCandidates)
            sb.Append("  …(").Append(matches.Count - MaxCandidates).Append(" more)\n");
        return sb.ToString();
    }

    private static string NoMatchMessage(string name, string fileName, IReadOnlyList<QualifiedSymbol> all)
    {
        var topLevel = all.Where(qs => qs.Symbol.Depth == 1 && !string.IsNullOrEmpty(qs.Symbol.Name))
            .Select(qs => qs.Symbol.Name)
            .Take(MaxHintNames)
            .ToList();
        var hint = topLevel.Count == 0
            ? " (no named top-level symbols found)"
            : " Top-level symbols: " + string.Join(", ", topLevel);
        return $"No symbol named '{name}' in {fileName}.{hint}";
    }
}
