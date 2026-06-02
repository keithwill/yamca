namespace Yamca.Agent.Subagents;

/// <summary>Single-slot holder the <see cref="SubagentRunner"/> hands to the per-run
/// <c>subagent_result</c> tool. When the subagent calls that tool, the payload lands here,
/// which is how the runner distinguishes a genuine answer from a subagent that flailed
/// (asked questions, hit a parse error, ran out of iterations) and never reported back.</summary>
public sealed class SubagentResultSink
{
    /// <summary>The result the subagent delivered, or <c>null</c> if it never called
    /// <c>subagent_result</c>.</summary>
    public string? Result { get; private set; }

    public bool HasResult { get; private set; }

    public void Deliver(string result)
    {
        Result = result ?? string.Empty;
        HasResult = true;
    }
}
