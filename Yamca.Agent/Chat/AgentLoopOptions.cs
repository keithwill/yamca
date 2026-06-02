namespace Yamca.Agent.Chat;

public sealed record AgentLoopOptions
{
    /// <summary>Maximum number of LLM round-trips per user turn. Hit when the model
    /// keeps producing tool calls without ever returning a plain reply.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>Identifies the chat session that owns this loop, stamped onto each
    /// <see cref="Yamca.Agent.Tools.ToolContext"/> so tools can attribute work back to
    /// the originating session. Null for loops not tied to a UI session (e.g. subagents).</summary>
    public string? OwnerId { get; init; }

    public static AgentLoopOptions Default { get; } = new();
}
