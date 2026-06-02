using Yamca.Agent.Subagents;

namespace Yamca.Web.Services;

/// <summary>Terminal state of a subagent run for UI display.</summary>
public enum SubagentRunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>A single subagent run, mirrored from <see cref="ISubagentObserver"/> events into a
/// <see cref="ChatTurn"/> so it can be rendered read-only with the same components as a real chat
/// turn. Held by <see cref="SubagentSessionRegistry"/>. Mutated from a background continuation
/// (the runner's loop) while the UI reads it; all mutable agent state lives on the thread-safe
/// <see cref="ChatTurn"/>, and the scalar fields below are written once on start/complete.</summary>
public sealed class SubagentLiveSession
{
    public SubagentLiveSession(SubagentRunInfo info)
    {
        RunId = info.RunId;
        ParentCallId = info.ParentCallId;
        OwnerId = info.OwnerId;
        AgentName = info.AgentName;
        Prompt = info.Prompt;
        StartedAt = info.StartedAt;
        Turn = new ChatTurn(info.Prompt);
    }

    public string RunId { get; }
    public string? ParentCallId { get; }

    /// <summary>The chat session that launched this run (<see cref="SubagentRunInfo.OwnerId"/>).</summary>
    public string? OwnerId { get; }

    public string AgentName { get; }
    public string Prompt { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public SubagentRunStatus Status { get; internal set; } = SubagentRunStatus.Running;

    /// <summary>The subagent's final answer (on success) or failure message (on error).</summary>
    public string? Result { get; internal set; }

    /// <summary>The subagent's streamed reasoning, tool calls, and assistant text, built up
    /// from the run's event stream.</summary>
    public ChatTurn Turn { get; }

    /// <summary>How long the run took, once it has completed.</summary>
    public TimeSpan? Duration => CompletedAt is { } end ? end - StartedAt : null;
}
