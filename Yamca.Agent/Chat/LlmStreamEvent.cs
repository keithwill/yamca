namespace Yamca.Agent.Chat;

/// <summary>LLM-client-level events emitted by an <see cref="IChatCompletionClient"/>.
/// The agent loop turns these into the higher-level <see cref="ChatStreamEvent"/>s.</summary>
public abstract record LlmStreamEvent;

/// <summary>An incremental assistant text fragment.</summary>
public sealed record LlmContentDelta(string Text) : LlmStreamEvent;

/// <summary>Final event for one assistant turn. The adapter is responsible for
/// aggregating fragmented tool-call deltas into completed requests before
/// emitting this.</summary>
public sealed record LlmAssistantTurnComplete(
    string Content,
    IReadOnlyList<LlmToolCallRequest> ToolCalls,
    string? FinishReason) : LlmStreamEvent;

public sealed record LlmToolCallRequest(string CallId, string ToolName, string ArgumentsJson);
