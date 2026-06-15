using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Permission identity for starting an <em>arbitrary</em> (non-registered) command as a
/// long-lived background process. Not exposed to the LLM — it exists so the settings table shows a
/// configurable row (default Ask) that the <c>start_process</c> tool resolves against when the
/// requested command is not a registered inline command. A registered command instead rides the
/// always-Allow <c>execute_allowed</c> permission. Never invoked directly.</summary>
public sealed class StartProcessCommandTool : ITool
{
    private const string Schema = """{ "type": "object", "properties": {}, "additionalProperties": false }""";

    public string Name => "start_process_command";

    public string Description =>
        "Starting an arbitrary (non-registered) command as a long-lived background process. " +
        "Invoked via the 'start_process' tool.";

    public string ParametersSchema => Schema;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool ExposedToLlm => false;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        => Task.FromResult(ToolResult.Error("Start background processes through the 'start_process' tool, not 'start_process_command' directly."));
}
