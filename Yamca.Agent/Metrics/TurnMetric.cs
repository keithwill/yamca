namespace Yamca.Agent.Metrics;

/// <summary>One throughput sample for a single model round-trip (one
/// <c>AgentLoop</c> iteration), persisted to the dedicated metrics store under
/// key <c>/metric/{Id}</c>. The dashboard plots generation/prompt speed against
/// the starting context size (<see cref="PromptTokens"/>), grouped by
/// endpoint·model, to show how a local model's throughput degrades as context grows.
///
/// Two accuracy tiers, distinguished by <see cref="TimingsFromServer"/>:
/// <list type="bullet">
/// <item>Tier A — llama-server's <c>timings</c> block reported the speeds directly
/// (authoritative; excludes network/queueing).</item>
/// <item>Tier B — the server reported token counts but no timings (OpenAI / vLLM),
/// so the agent loop derived the speeds from client-side wall-clock measurement.</item>
/// </list>
/// A plain record with no VestPocket base type — the store registers it via
/// <c>AddType&lt;TurnMetric&gt;()</c> and a matching <c>[JsonSerializable]</c> line.</summary>
public sealed record TurnMetric(
    string Id,
    DateTimeOffset TimestampUtc,
    string? SessionId,
    Guid EndpointId,
    string EndpointName,
    string Model,
    int PromptTokens,
    int? CachedTokens,
    int CompletionTokens,
    double? PromptPerSecond,
    double? PredictedPerSecond,
    double PromptMs,
    double PredictedMs,
    bool TimingsFromServer,
    // Denormalized snapshot of the endpoint's base URL (like EndpointName/Model), used as the
    // series label when the endpoint has neither a name nor a model — far more recognizable than a
    // bare endpoint id. Null on samples recorded before this field existed. Trailing + optional so
    // existing positional/target-typed constructions keep compiling.
    string? EndpointBaseUrl = null);
