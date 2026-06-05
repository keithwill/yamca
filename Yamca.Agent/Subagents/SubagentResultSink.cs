namespace Yamca.Agent.Subagents;

/// <summary>The outcome a subagent declares when it calls <c>subagent_result</c>. The subagent
/// is already reasoning about whether it succeeded, so having it state this costs nothing extra
/// and lets callers (single runs and batch loops alike) branch on success/failure mechanically,
/// without an outer model reading the prose.</summary>
public enum SubagentStatus
{
    /// <summary>The task was accomplished.</summary>
    Success,

    /// <summary>The subagent ran but concluded it could not accomplish the task.</summary>
    Failure,

    /// <summary>The task ran fine, but this item needs another look (the catch-all for
    /// "succeeded mechanically, but flag it").</summary>
    NeedsFollowup,
}

/// <summary>Single-slot holder the <see cref="SubagentRunner"/> hands to the per-run
/// <c>subagent_result</c> tool. When the subagent calls that tool, the status and payload land
/// here, which is how the runner distinguishes a genuine answer from a subagent that flailed
/// (asked questions, hit a parse error, ran out of iterations) and never reported back.</summary>
public sealed class SubagentResultSink
{
    /// <summary>The result the subagent delivered, or <c>null</c> if it never called
    /// <c>subagent_result</c>.</summary>
    public string? Result { get; private set; }

    /// <summary>The status the subagent declared. Meaningful only when <see cref="HasResult"/>.</summary>
    public SubagentStatus Status { get; private set; }

    public bool HasResult { get; private set; }

    public void Deliver(SubagentStatus status, string result)
    {
        Status = status;
        Result = result ?? string.Empty;
        HasResult = true;
    }
}
