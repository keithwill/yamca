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
        "once, when you have finished the task. Report a 'status' (success, failure, or " +
        "needs_followup) so the caller knows the outcome without re-reading your answer, and the " +
        "'result' text — the only thing the caller receives, so make it self-contained.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "status": {
          "type": "string",
          "enum": ["success", "failure", "needs_followup"],
          "description": "Outcome of the task: 'success' = you accomplished it; 'failure' = you could not accomplish it; 'needs_followup' = it ran fine but this one needs another look."
        },
        "result": { "type": "string", "description": "The complete answer to return to the caller." }
      },
      "required": ["status", "result"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "status", out var statusText, out var statusError))
            return Task.FromResult(ToolResult.Error(statusError));
        if (!TryParseStatus(statusText, out var status))
            return Task.FromResult(ToolResult.Error(
                $"Argument 'status' must be one of: success, failure, needs_followup (got '{statusText}')."));
        if (!ToolArguments.TryGetString(arguments, "result", out var result, out var error))
            return Task.FromResult(ToolResult.Error(error));

        _sink.Deliver(status, result);
        return Task.FromResult(ToolResult.Ok("Result delivered to the caller."));
    }

    private static bool TryParseStatus(string text, out SubagentStatus status)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "success": status = SubagentStatus.Success; return true;
            case "failure": status = SubagentStatus.Failure; return true;
            case "needs_followup": status = SubagentStatus.NeedsFollowup; return true;
            default: status = SubagentStatus.Failure; return false;
        }
    }
}
