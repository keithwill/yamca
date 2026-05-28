using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TreeSitter;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

/// <summary>
/// AST-aware grep: matches a regex against node text but only for nodes of a chosen kind
/// (identifiers, strings, comments, or call targets). The "find uses of Run but not the 600
/// hits inside comments/strings" case that raw grep can't express.
/// </summary>
public sealed class CodeSearchTool : ITool
{
    private const int MaxLineLength = 1000;

    private readonly NodeProfileResolver _resolver;

    public CodeSearchTool(NodeProfileResolver resolver)
    {
        _resolver = resolver;
    }

    public string Name => "code_search";

    public string Description => "Search code with a regex restricted to one AST node class via 'in': identifiers (default), strings, comments, or calls. Unlike grep, an identifier search won't match the same text inside comments or string literals. Results: path:line:line-content.";

    public string ParametersSchema => $$"""
    {
      "type": "object",
      "properties": {
        "pattern": { "type": "string", "description": ".NET regex tested against each node's text." },
        "in":      { "type": "string", "enum": ["identifiers", "strings", "comments", "calls"], "description": "Node class to match within. Default 'identifiers'." },
        "case_insensitive": { "type": "boolean", "description": "Case-insensitive match. Default false." },
        {{ScanScope.SchemaProperties}}
      },
      "required": ["pattern"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;
    public PermissionLevel DefaultPermission => PermissionLevel.Allow;
    public bool Deferred => true;

    private enum NodeClass { Identifiers, Strings, Comments, Calls }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "pattern", out var pattern, out var argError))
            return ToolResult.Error(argError);

        var nodeClass = NodeClass.Identifiers;
        if (arguments.TryGetProperty("in", out var inProp) && inProp.ValueKind == JsonValueKind.String)
        {
            nodeClass = inProp.GetString() switch
            {
                "identifiers" => NodeClass.Identifiers,
                "strings" => NodeClass.Strings,
                "comments" => NodeClass.Comments,
                "calls" => NodeClass.Calls,
                _ => (NodeClass)(-1),
            };
            if ((int)nodeClass < 0)
                return ToolResult.Error("'in' must be one of: identifiers, strings, comments, calls.");
        }

        var caseInsensitive = false;
        if (arguments.TryGetProperty("case_insensitive", out var ciProp) &&
            (ciProp.ValueKind == JsonValueKind.True || ciProp.ValueKind == JsonValueKind.False))
            caseInsensitive = ciProp.GetBoolean();

        if (!ScanScope.TryParse(arguments, context, out var scope, out var scopeError))
            return ToolResult.Error(scopeError);

        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (caseInsensitive) options |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error($"Invalid regex: {ex.Message}");
        }

        var output = new StringBuilder();
        var count = 0;
        var truncated = false;

        try
        {
            await CodeScan.RunAsync(scope.Root, scope.Glob, scope.RespectGitignore, _resolver, (rel, source, root, profile) =>
            {
                var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                var seenLines = new HashSet<int>();

                foreach (var node in CodeScan.NamedDescendants(root))
                {
                    if (!TryMatchClass(node, profile, nodeClass, regex)) continue;

                    var line = node.StartPosition.Row + 1;
                    if (!seenLines.Add(line)) continue;

                    var text = line - 1 < lines.Length ? lines[line - 1].Trim() : node.Text ?? string.Empty;
                    if (text.Length > MaxLineLength) text = text[..MaxLineLength] + "…";

                    output.Append(rel).Append(':').Append(line).Append(':').Append(text).Append('\n');

                    if (++count >= scope.MaxMatches) { truncated = true; return false; }
                }
                return true;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("code_search cancelled.");
        }
        catch (RegexMatchTimeoutException)
        {
            return ToolResult.Error("code_search regex timed out.");
        }

        if (count == 0)
            return ToolResult.Ok("(no matches)");

        if (truncated) output.Append("…[truncated at ").Append(scope.MaxMatches).Append(" matches]\n");
        return ToolResult.Ok(output.ToString());
    }

    private static bool TryMatchClass(Node node, ILanguageNodeProfile profile, NodeClass cls, Regex regex)
    {
        switch (cls)
        {
            case NodeClass.Identifiers:
                return profile.IsIdentifier(node.Type) && regex.IsMatch(node.Text ?? string.Empty);
            case NodeClass.Strings:
                return profile.IsString(node.Type) && regex.IsMatch(node.Text ?? string.Empty);
            case NodeClass.Comments:
                return profile.IsComment(node.Type) && regex.IsMatch(node.Text ?? string.Empty);
            case NodeClass.Calls:
                return profile.TryGetCall(node, out var callee) && regex.IsMatch(callee);
            default:
                return false;
        }
    }
}
