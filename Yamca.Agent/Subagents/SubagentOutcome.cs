using Yamca.Agent.Chat;

namespace Yamca.Agent.Subagents;

/// <summary>The structured result of a single headless subagent run, produced by
/// <see cref="SubagentRunner.RunCoreAsync"/>. Both the single-run <c>subagent_run</c> tool and
/// the batch engine consume this: the former maps it to a <see cref="Tools.ToolResult"/>, the
/// latter aggregates many into a mechanical roll-up.
///
/// Two kinds of failure are distinguished. A <see cref="SubagentStatus.Failure"/> with
/// <see cref="Delivered"/> set is a <em>semantic</em> failure — the subagent ran fine but
/// concluded it could not accomplish the task. A <see cref="MechanicalFailure"/> is the runner's
/// own verdict — the subagent stalled, hit its iteration cap, or was cancelled and never reported
/// back. Both count as a failed item.</summary>
/// <param name="Delivered">Whether the subagent called <c>subagent_result</c>.</param>
/// <param name="Status">The status the subagent declared; meaningful only when <paramref name="Delivered"/>.</param>
/// <param name="Summary">The delivered result text, or — for a mechanical failure — the runner's failure message.</param>
/// <param name="MechanicalFailure">The run ended without a delivered result (no-delivery / cap / cancel).</param>
/// <param name="Reason">How the turn ended, for mechanical failures.</param>
public sealed record SubagentOutcome(
    bool Delivered,
    SubagentStatus Status,
    string Summary,
    bool MechanicalFailure,
    TurnCompletionReason? Reason)
{
    /// <summary>The item failed: either a mechanical failure or a declared semantic failure.</summary>
    public bool IsFailure => MechanicalFailure || (Delivered && Status == SubagentStatus.Failure);

    /// <summary>The item ran fine but the subagent flagged it for another look.</summary>
    public bool IsNeedsFollowup => Delivered && !MechanicalFailure && Status == SubagentStatus.NeedsFollowup;

    /// <summary>The item was accomplished.</summary>
    public bool IsSuccess => Delivered && !MechanicalFailure && Status == SubagentStatus.Success;
}
