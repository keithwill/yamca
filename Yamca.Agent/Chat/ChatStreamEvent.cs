namespace Yamca.Agent.Chat;

/// <summary>UI-facing stream of events for a single user turn. The Blazor chat
/// component renders message bubbles, tool-call cards, and approval prompts
/// off this stream.</summary>
public abstract record ChatStreamEvent;

/// <summary>A request to the model has just been dispatched; the loop is now waiting
/// for the server to process the prompt (the system/user messages or the latest tool
/// results) before any token streams back. The UI uses this to show a "Thinking"
/// indicator during the otherwise-silent prompt-processing gap.</summary>
public sealed record ModelRequestStartedEvent : ChatStreamEvent
{
    public static readonly ModelRequestStartedEvent Instance = new();
}

/// <summary>Incremental assistant text token.</summary>
public sealed record AssistantTokenEvent(string Delta) : ChatStreamEvent;

/// <summary>Incremental reasoning / chain-of-thought token (extracted from
/// inline <c>&lt;think&gt;</c>-style tags by the completion client).</summary>
public sealed record ReasoningTokenEvent(string Delta) : ChatStreamEvent;

/// <summary>The reasoning block has finished (closing tag observed). The UI
/// uses this to auto-collapse the streaming reasoning panel.</summary>
public sealed record ReasoningCompleteEvent : ChatStreamEvent
{
    public static readonly ReasoningCompleteEvent Instance = new();
}

/// <summary>Assistant produced a complete message (with optional tool calls).
/// Emitted once per assistant turn after the LLM finishes generating.</summary>
public sealed record AssistantMessageEvent(
    string Content,
    IReadOnlyList<LlmToolCallRequest> ToolCalls) : ChatStreamEvent;

/// <summary>The model has begun streaming one or more tool calls, but none has finished
/// generating yet. Emitted before <see cref="AssistantMessageEvent"/> so the UI can show a
/// "generating tool call" indicator during the (often slow) argument-streaming gap, instead
/// of leaving the prompt-processing indicator up until execution starts.</summary>
public sealed record ToolCallGenerationStartedEvent : ChatStreamEvent
{
    public static readonly ToolCallGenerationStartedEvent Instance = new();
}

/// <summary>The agent is about to invoke a tool (permission already cleared).</summary>
public sealed record ToolCallStartedEvent(string CallId, string ToolName, string ArgumentsJson) : ChatStreamEvent;

/// <summary>A tool finished executing.</summary>
public sealed record ToolCallResultEvent(string CallId, string ToolName, bool IsError, string Content) : ChatStreamEvent;

/// <summary>Permission denied (either by settings or by the user at approval time).
/// The tool was NOT executed; the error is fed back to the model as a tool message.</summary>
public sealed record ToolDeniedEvent(string CallId, string ToolName, string Reason) : ChatStreamEvent;

/// <summary>Server-reported token usage for the in-flight assistant turn.
/// Surfaced when the OpenAI-compatible server emits a usage chunk
/// (<c>stream_options.include_usage</c> on OpenAI / vLLM, or llama-server's
/// terminal <c>usage</c>+<c>timings</c> SSE frame). Lets the UI show actual
/// prompt-token counts instead of our char/4 estimate.</summary>
public sealed record UsageUpdateEvent(
    int PromptTokens,
    int CompletionTokens,
    int? CachedTokens) : ChatStreamEvent;

/// <summary>The whole user turn has finished — assistant produced a plain reply,
/// or the iteration cap was hit, or the user cancelled.</summary>
public sealed record TurnCompleteEvent(TurnCompletionReason Reason) : ChatStreamEvent;

public enum TurnCompletionReason
{
    AssistantReply,
    MaxIterationsReached,
    Cancelled,
}
