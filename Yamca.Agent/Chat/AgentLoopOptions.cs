namespace Yamca.Agent.Chat;

public sealed record AgentLoopOptions
{
    /// <summary>Maximum number of LLM round-trips per user turn. Hit when the model
    /// keeps producing tool calls without ever returning a plain reply.</summary>
    public int MaxIterations { get; init; } = 10;

    public static AgentLoopOptions Default { get; } = new();
}
