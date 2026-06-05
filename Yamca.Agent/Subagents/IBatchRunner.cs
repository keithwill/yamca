using Yamca.Agent.Tools;

namespace Yamca.Agent.Subagents;

/// <summary>Map-reduce over <see cref="ISubagentRunner"/>: runs one prompt across many items,
/// each in its own isolated subagent session, then reduces the per-item outcomes to a single
/// mechanical roll-up. Factored apart from any one caller so the LLM-facing <c>loop</c> tool and
/// future user-initiated faces (board batch actions, a CLI, a directory picker) can all drive it.</summary>
public interface IBatchRunner
{
    /// <summary>Run <paramref name="agentName"/> once per item in <paramref name="items"/>, joining
    /// each item onto <paramref name="promptTemplate"/>, and return a roll-up of the outcomes. The
    /// roll-up is built mechanically from declared statuses — no outer model call.</summary>
    Task<ToolResult> RunAsync(
        string agentName,
        string promptTemplate,
        IReadOnlyList<string> items,
        ToolContext parentContext,
        CancellationToken cancellationToken);
}
