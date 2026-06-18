namespace Yamca.Agent.Chat;

public sealed record AgentLoopOptions
{
    /// <summary>Maximum number of LLM round-trips per user turn. Hit when the model
    /// keeps producing tool calls without ever returning a plain reply.</summary>
    public int MaxIterations { get; init; } = 30;

    /// <summary>Identifies the chat session that owns this loop, stamped onto each
    /// <see cref="Yamca.Agent.Tools.ToolContext"/> so tools can attribute work back to
    /// the originating session. Null for loops not tied to a UI session (e.g. subagents).</summary>
    public string? OwnerId { get; init; }

    /// <summary>Identity of the endpoint this loop runs against, stamped onto each
    /// <see cref="Yamca.Agent.Metrics.TurnMetric"/> so the throughput dashboard can group
    /// samples by endpoint·model. Left at their defaults when metrics aren't being recorded.</summary>
    public Guid EndpointId { get; init; }
    public string EndpointName { get; init; } = "";
    public string Model { get; init; } = "";
    public string EndpointBaseUrl { get; init; } = "";

    /// <summary>Whether this loop emits a <see cref="Yamca.Agent.Metrics.TurnMetric"/> per
    /// round-trip. Carries the user's "Record throughput metrics" preference to the loop the same
    /// way <see cref="MaxIterations"/> does: when false, recording is a no-op even if a sink is
    /// wired. Defaults to true so a loop built without the preference still records.</summary>
    public bool RecordMetrics { get; init; } = true;

    public static AgentLoopOptions Default { get; } = new();
}
