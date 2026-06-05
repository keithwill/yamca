using Yamca.Agent.Chat;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Subagents;

/// <summary>Runs a configured subagent as a headless <see cref="AgentLoop"/> and returns its
/// result to the caller. Scoped per circuit; <see cref="Bind"/> is called by the chat
/// view-model with the parent's completion client so subagents inherit the parent's endpoint
/// by default.</summary>
public interface ISubagentRunner
{
    /// <summary>Supply the parent chat's completion client. A subagent reuses it unless it
    /// specifies its own endpoint override.</summary>
    void Bind(IChatCompletionClient parentClient);

    /// <summary>Look up <paramref name="agentName"/>, run it headless against
    /// <paramref name="parentContext"/>'s workspace with <paramref name="prompt"/>, and return
    /// either the result the subagent delivered via <c>subagent_result</c> or an error
    /// describing why it produced none. Thin wrapper over <see cref="RunCoreAsync"/> that maps
    /// the structured outcome to a <see cref="ToolResult"/> for the single-delegation case.</summary>
    Task<ToolResult> RunAsync(
        string agentName,
        string prompt,
        ToolContext parentContext,
        CancellationToken cancellationToken);

    /// <summary>The core run, returning the structured <see cref="SubagentOutcome"/> consumed by
    /// the batch engine. <paramref name="loopRunId"/> groups this run under a parent loop in the
    /// observability pipeline (null for a standalone <c>subagent_run</c>).</summary>
    Task<SubagentOutcome> RunCoreAsync(
        string agentName,
        string prompt,
        ToolContext parentContext,
        string? loopRunId,
        CancellationToken cancellationToken);
}
