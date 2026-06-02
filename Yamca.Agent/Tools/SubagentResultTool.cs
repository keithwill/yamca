using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Subagents;

namespace Yamca.Agent.Tools;

/// <summary>The tool a subagent calls to deliver its final answer to the caller. It is never
/// registered in DI — the <see cref="SubagentRunner"/> constructs one per run, bound to a
/// <see cref="SubagentResultSink"/>, and adds it to the subagent's private tool set. A normal
/// parent chat therefore never sees it. Calling it is the explicit signal that the subagent
/// produced a real result rather than confused output.</summary>
public sealed class SubagentResultTool : ITool
{
    public const string ToolName = "subagent_result";

    private readonly SubagentResultSink _sink;

    public SubagentResultTool(SubagentResultSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public string Name => ToolName;

    public string Description =>
        "Deliver your final answer to the caller and end the subagent run. Call this exactly " +
        "once, when you have finished the task. The 'result' text is the only thing the caller " +
        "receives — make it self-contained.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "result": { "type": "string", "description": "The complete answer to return to the caller." }
      },
      "required": ["result"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "result", out var result, out var error))
            return Task.FromResult(ToolResult.Error(error));

        _sink.Deliver(result);
        return Task.FromResult(ToolResult.Ok("Result delivered to the caller."));
    }
}
