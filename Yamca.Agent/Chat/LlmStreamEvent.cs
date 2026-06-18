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

/// <summary>Emitted once, the first time a <c>tool_calls</c> delta is observed in the
/// stream. The model has begun generating one or more tool calls (which may take a while
/// to stream in full) but no call has completed yet. Lets the UI show a "generating tool
/// call" indicator during the otherwise-silent gap before execution begins.</summary>
public sealed record LlmToolCallStreamStarted : LlmStreamEvent
{
    public static readonly LlmToolCallStreamStarted Instance = new();
}

/// <summary>Streaming token-usage snapshot reported by the server. Emitted
/// once per assistant turn when the server supports it (OpenAI
/// <c>stream_options.include_usage</c>, llama-server's <c>timings</c>/<c>usage</c>
/// trailer, vLLM's terminal usage chunk). Lets the UI display real prompt-token
/// totals rather than our char/4 estimate. <paramref name="CachedTokens"/> is
/// the llama-server prompt-cache hit count when available.
///
/// The speed fields (<paramref name="PromptPerSecond"/>,
/// <paramref name="PredictedPerSecond"/>, <paramref name="PromptMs"/>,
/// <paramref name="PredictedMs"/>) come from llama-server's <c>timings</c> block
/// and are the authoritative (Tier A) source for the throughput dashboard.
/// They are null for servers that don't report timings (OpenAI / vLLM), where
/// the agent loop falls back to client-side wall-clock measurement (Tier B).</summary>
public sealed record LlmUsageUpdate(
    int PromptTokens,
    int CompletionTokens,
    int? CachedTokens = null,
    double? PromptPerSecond = null,
    double? PredictedPerSecond = null,
    double? PromptMs = null,
    double? PredictedMs = null) : LlmStreamEvent;

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
