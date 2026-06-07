using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Git;

/// <summary>Permission identity for read-only git subcommands. Not exposed to the LLM — it
/// exists so the settings table shows a configurable row (default Allow) that the <c>git</c>
/// tool resolves against. Never invoked directly.</summary>
public sealed class GitReadTool : ITool
{
    private const string Schema = """{ "type": "object", "properties": {}, "additionalProperties": false }""";

    public string Name => "git_read";

    public string Description =>
        "Read-only git subcommands (" + GitSubcommands.ReadList + "). Invoked via the 'git' tool.";

    public string ParametersSchema => Schema;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedToLlm => false;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        => Task.FromResult(ToolResult.Error("Run git operations through the 'git' tool, not 'git_read' directly."));
}

/// <summary>Permission identity for mutating git subcommands. Not exposed to the LLM — it exists
/// so the settings table shows a configurable row (default Ask) that the <c>git</c> tool resolves
/// against. Never invoked directly.</summary>
public sealed class GitWriteTool : ITool
{
    private const string Schema = """{ "type": "object", "properties": {}, "additionalProperties": false }""";

    public string Name => "git_write";

    public string Description =>
        "Mutating git subcommands (" + GitSubcommands.WriteList + "). Invoked via the 'git' tool.";

    public string ParametersSchema => Schema;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool ExposedToLlm => false;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        => Task.FromResult(ToolResult.Error("Run git operations through the 'git' tool, not 'git_write' directly."));
}
