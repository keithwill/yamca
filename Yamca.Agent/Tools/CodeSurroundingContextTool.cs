using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

public sealed class CodeSurroundingContextTool : ITool
{
    private readonly SymbolService _symbols;

    public CodeSurroundingContextTool(SymbolService symbols)
    {
        _symbols = symbols;
    }

    public string Name => "code_surrounding_context";

    public string Description => "Given a 1-indexed line number (typically from a grep/code_search hit), report the enclosing function/class signature and its container path, so a noisy line match becomes 'this is method Bar inside class Foo'. Use code_extract_symbol to then read the whole symbol.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string",  "description": "Source file path (workspace-relative or absolute)." },
        "line": { "type": "integer", "description": "1-indexed line number to locate within the file.", "minimum": 1 }
      },
      "required": ["path", "line"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);
        if (!arguments.TryGetProperty("line", out var lineProp) || lineProp.ValueKind != JsonValueKind.Number)
            return ToolResult.Error("'line' is required and must be an integer.");
        var line = lineProp.GetInt32();
        if (line < 1)
            return ToolResult.Error("'line' must be 1 or greater.");

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);
        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        var load = await _symbols.LoadAsync(resolved, cancellationToken);
        if (!load.IsOk)
            return ToolResult.Error(load.Note ?? $"Could not read symbols from '{resolved}'.");

        var fileName = Path.GetFileName(resolved);
        var qualified = SymbolLookup.Qualify(load.Symbols);

        // Deepest symbol whose span [Line, EndLine] contains the target line.
        QualifiedSymbol? enclosing = null;
        foreach (var qs in qualified)
        {
            if (qs.Symbol.Line <= line && line <= qs.Symbol.EndLine)
            {
                if (enclosing is null || qs.Symbol.Depth > enclosing.Value.Symbol.Depth)
                    enclosing = qs;
            }
        }

        var sb = new StringBuilder();
        sb.Append(fileName).Append(':').Append(line).Append('\n');

        if (enclosing is { } hit)
        {
            var chainParts = hit.Ancestors.Select(a => a.Display).Append(hit.Symbol.Display);
            sb.Append("  in: ").Append(string.Join(" > ", chainParts)).Append('\n');
            sb.Append("  ").Append(hit.Symbol.Kind).Append(' ').Append(hit.QualifiedName)
              .Append("  [L").Append(hit.Symbol.Line).Append('-').Append(hit.Symbol.EndLine).Append("]\n");
        }
        else
        {
            // No symbol spans the line — name the nearest preceding top-level symbol if any.
            var preceding = qualified
                .Where(qs => qs.Symbol.Line <= line)
                .OrderByDescending(qs => qs.Symbol.Line)
                .FirstOrDefault();
            if (preceding.Symbol is not null)
                sb.Append("  (not inside a symbol; nearest above: ").Append(preceding.Symbol.Display)
                  .Append("  [L").Append(preceding.Symbol.Line).Append("])\n");
            else
                sb.Append("  (not inside any symbol; file-level / before first symbol)\n");
        }

        return ToolResult.Ok(sb.ToString());
    }
}
