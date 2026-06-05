using Yamca.Agent.Chat;

namespace Yamca.Agent.Subagents;

/// <summary>Identifying details for a single subagent run, emitted when it starts.</summary>
/// <param name="RunId">Stable id for this run (unique per invocation).</param>
/// <param name="ParentCallId">The parent tool-call id that launched this run, when the
/// subagent was invoked from a live chat. Lets the UI correlate a <c>subagent_run</c>
/// card to its transcript. Null when invoked outside the agent loop.</param>
/// <param name="OwnerId">Identifies the chat session that launched the run, so the UI can
/// show it only in that session. Null when not invoked from a UI session.</param>
/// <param name="AgentName">The configured subagent's name.</param>
/// <param name="Prompt">The self-contained task handed to the subagent.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="LoopRunId">The loop run that spawned this run, when it was launched as one item
/// of a batch <c>loop</c>. Lets the UI group a loop's child runs together. Null for a standalone
/// <c>subagent_run</c>.</param>
public sealed record SubagentRunInfo(
    string RunId,
    string? ParentCallId,
    string? OwnerId,
    string AgentName,
    string Prompt,
    DateTimeOffset StartedAt,
    string? LoopRunId = null);

/// <summary>Receives the live event stream of a headless subagent run so a host (the
/// Blazor UI) can mirror it into a viewable, read-only session. <see cref="SubagentRunner"/>
/// already enumerates the child <see cref="AgentLoop"/>'s events to detect the result;
/// it forwards each one here as well. Implementations must be safe to call from a
/// background continuation (events arrive off the UI dispatcher).</summary>
public interface ISubagentObserver
{
    /// <summary>A run has begun. Always followed by a matching <see cref="OnCompleted"/>.</summary>
    void OnStarted(SubagentRunInfo info);

    /// <summary>One streamed event from the subagent's loop (assistant text, reasoning,
    /// tool call started/result, etc.).</summary>
    void OnEvent(string runId, ChatStreamEvent ev);

    /// <summary>A snapshot of the exact request the subagent would send (system message, tools,
    /// messages, raw JSON), for the "view raw context" diagnostic. Sent once at launch (so a
    /// running subagent can show at least its system prompt and tools) and refreshed once on
    /// completion with the full final context. Hosts that don't surface it can ignore it.</summary>
    void OnContext(string runId, ChatRequestPreview context);

    /// <summary>The run has finished — with a delivered result, a failure, or cancellation.
    /// Guaranteed to fire exactly once per <see cref="OnStarted"/>.</summary>
    void OnCompleted(string runId, bool isError, string result);
}

/// <summary>Default observer that drops everything. Used when no host is wired
/// (e.g. tests or non-UI composition roots).</summary>
public sealed class NoopSubagentObserver : ISubagentObserver
{
    public static readonly NoopSubagentObserver Instance = new();

    public void OnStarted(SubagentRunInfo info) { }
    public void OnEvent(string runId, ChatStreamEvent ev) { }
    public void OnContext(string runId, ChatRequestPreview context) { }
    public void OnCompleted(string runId, bool isError, string result) { }
}
