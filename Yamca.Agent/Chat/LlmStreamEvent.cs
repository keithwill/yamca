namespace Yamca.Agent.Chat;

/// <summary>LLM-client-level events emitted by an <see cref="IChatCompletionClient"/>.
/// The agent loop turns these into the higher-level <see cref="ChatStreamEvent"/>s.</summary>
public abstract record LlmStreamEvent;

/// <summary>An incremental assistant text fragment.</summary>
public sealed record LlmContentDelta(string Text) : LlmStreamEvent;

/// <summary>An incremental fragment of model reasoning / chain-of-thought,
/// extracted from inline tags like <c>&lt;think&gt;...&lt;/think&gt;</c> in the
/// content stream. Not appended to session history.</summary>
public sealed record LlmReasoningDelta(string Text) : LlmStreamEvent;

/// <summary>Emitted once the closing reasoning tag arrives, signalling the UI
/// to auto-collapse the reasoning block.</summary>
public sealed record LlmReasoningClose : LlmStreamEvent
{
    public static readonly LlmReasoningClose Instance = new();
}

/// <summary>Final event for one assistant turn. The adapter is responsible for
/// aggregating fragmented tool-call deltas into completed requests before
/// emitting this. <paramref name="Reasoning"/> is the full concatenated reasoning
/// text (may be empty); it is provided for completeness and is not sent back to
/// the model in subsequent turns.</summary>
public sealed record LlmAssistantTurnComplete(
    string Content,
    IReadOnlyList<LlmToolCallRequest> ToolCalls,
    string? FinishReason,
    string Reasoning = "") : LlmStreamEvent;

public sealed record LlmToolCallRequest(string CallId, string ToolName, string ArgumentsJson);
