using System.Text;
using System.Text.Json;
using TreeSitter;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

/// <summary>
/// Shared plumbing for the lexical AST search tools (code_find_definitions / _calls /
/// _references): parse a <c>name</c> + scan scope, walk every routed file's tree, and emit
/// <c>path:line  [label]  in &lt;container&gt;</c> per match. Subclasses supply the per-node
/// match predicate. These are lexical, not semantic — a unique-ish name is the sweet spot.
/// </summary>
public abstract class CodeFindToolBase : ITool
{
    private readonly NodeProfileResolver _resolver;

    protected CodeFindToolBase(NodeProfileResolver resolver)
    {
        _resolver = resolver;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }

    public string ParametersSchema => $$"""
    {
      "type": "object",
      "properties": {
        "name": { "type": "string", "description": "Symbol name to find (leaf identifier)." },
        {{ScanScope.SchemaProperties}}
      },
      "required": ["name"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;
    public PermissionLevel DefaultPermission => PermissionLevel.Allow;
    public bool Deferred => true;

    /// <summary>
    /// Returns true and a (possibly empty) display label when <paramref name="node"/> matches
    /// <paramref name="name"/> for this tool's search kind.
    /// </summary>
    protected abstract bool TryMatch(Node node, ILanguageNodeProfile profile, string name, out string label);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var argError))
            return ToolResult.Error(argError);
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("'name' must not be empty.");
        if (!ScanScope.TryParse(arguments, context, out var scope, out var scopeError))
            return ToolResult.Error(scopeError);

        var output = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var truncated = false;

        try
        {
            await CodeScan.RunAsync(scope.Root, scope.Glob, scope.RespectGitignore, _resolver, (rel, _, root, profile) =>
            {
                foreach (var node in CodeScan.NamedDescendants(root))
                {
                    if (!TryMatch(node, profile, name, out var label)) continue;

                    var line = node.StartPosition.Row + 1;
                    var container = NodeHeuristics.EnclosingPath(node, profile);
                    var key = $"{rel}:{line}:{label}";
                    if (!seen.Add(key)) continue;

                    output.Append(rel).Append(':').Append(line);
                    if (label.Length > 0) output.Append("  ").Append(label);
                    output.Append("  in ").Append(container.Length == 0 ? "(top level)" : container).Append('\n');

                    if (++count >= scope.MaxMatches) { truncated = true; return false; }
                }
                return true;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"{Name} cancelled.");
        }

        if (count == 0)
            return ToolResult.Ok($"(no matches for '{name}')");

        if (truncated) output.Append("…[truncated at ").Append(scope.MaxMatches).Append(" matches]\n");
        return ToolResult.Ok(output.ToString());
    }
}

public sealed class CodeFindDefinitionsTool : CodeFindToolBase
{
    public CodeFindDefinitionsTool(NodeProfileResolver resolver) : base(resolver) { }

    public override string Name => "code_find_definitions";
    public override string Description => "Find declarations of a named symbol (class/function/method/etc.) across the workspace using the parse tree, so matches inside comments and strings are excluded — unlike raw grep. Lexical, not semantic: best for unique-ish names. Results: path:line  kind name  in <container>.";

    protected override bool TryMatch(Node node, ILanguageNodeProfile profile, string name, out string label)
    {
        label = string.Empty;
        if (!profile.TryGetDefinition(node, out var defName, out var kind) || !string.Equals(defName, name, StringComparison.Ordinal))
            return false;
        label = $"{kind} {defName}";
        return true;
    }
}

public sealed class CodeFindCallsTool : CodeFindToolBase
{
    public CodeFindCallsTool(NodeProfileResolver resolver) : base(resolver) { }

    public override string Name => "code_find_calls";
    public override string Description => "Find call sites of a named function/method across the workspace using the parse tree (skips comments and strings, unlike raw grep). Lexical, not semantic. Results: path:line  in <container>.";

    protected override bool TryMatch(Node node, ILanguageNodeProfile profile, string name, out string label)
    {
        label = string.Empty;
        return profile.TryGetCall(node, out var callee) && string.Equals(callee, name, StringComparison.Ordinal);
    }
}

public sealed class CodeFindReferencesTool : CodeFindToolBase
{
    public CodeFindReferencesTool(NodeProfileResolver resolver) : base(resolver) { }

    public override string Name => "code_find_references";
    public override string Description => "Find every identifier occurrence of a name across the workspace using the parse tree. Identifier matches never include comment or string text, which is the durable win over grep. Broader than code_find_definitions / code_find_calls. Results: path:line  in <container>.";

    protected override bool TryMatch(Node node, ILanguageNodeProfile profile, string name, out string label)
    {
        label = string.Empty;
        return profile.IsIdentifier(node.Type) && string.Equals(node.Text, name, StringComparison.Ordinal);
    }
}
