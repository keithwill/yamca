namespace Yamca.Agent.Chat;

/// <summary>UI-facing stream of events for a single user turn. The Blazor chat
/// component renders message bubbles, tool-call cards, and approval prompts
/// off this stream.</summary>
public abstract record ChatStreamEvent;

/// <summary>Incremental assistant text token.</summary>
public sealed record AssistantTokenEvent(string Delta) : ChatStreamEvent;

/// <summary>Assistant produced a complete message (with optional tool calls).
/// Emitted once per assistant turn after the LLM finishes generating.</summary>
public sealed record AssistantMessageEvent(
    string Content,
    IReadOnlyList<LlmToolCallRequest> ToolCalls) : ChatStreamEvent;

/// <summary>The agent is about to invoke a tool (permission already cleared).</summary>
public sealed record ToolCallStartedEvent(string CallId, string ToolName, string ArgumentsJson) : ChatStreamEvent;

/// <summary>A tool finished executing.</summary>
public sealed record ToolCallResultEvent(string CallId, string ToolName, bool IsError, string Content) : ChatStreamEvent;

/// <summary>Permission denied (either by settings or by the user at approval time).
/// The tool was NOT executed; the error is fed back to the model as a tool message.</summary>
public sealed record ToolDeniedEvent(string CallId, string ToolName, string Reason) : ChatStreamEvent;

/// <summary>The whole user turn has finished — assistant produced a plain reply,
/// or the iteration cap was hit, or the user cancelled.</summary>
public sealed record TurnCompleteEvent(TurnCompletionReason Reason) : ChatStreamEvent;

public enum TurnCompletionReason
{
    AssistantReply,
    MaxIterationsReached,
    Cancelled,
}
