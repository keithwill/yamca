using Yamca.Agent.Chat;

namespace Yamca.Agent.Orchestration;

/// <summary>Where a card stands with the orchestrator. Queued/Running are "claimed" (the card
/// will not be re-dispatched); Retrying waits for its due time; Parked is terminal until the
/// user moves/edits the card or toggles the orchestrator. Succeeded/Failed/Cancelled appear
/// only on run records (the state table drops entries once they resolve).</summary>
public enum CardRunStatus
{
    Queued,
    Running,
    Retrying,
    Parked,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>The orchestrator's per-card tracking entry — the claimed set plus retry/park
/// bookkeeping. Mutated only by the orchestrator loop (single-writer); the UI reads immutable
/// snapshots.</summary>
public sealed record CardOrchestrationState(
    string CardId,
    string ColumnDirectory,
    CardRunStatus Status,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? FailureReason,
    string? ActiveRunId);

/// <summary>Terminal classification of one orchestrated run attempt.</summary>
public sealed record OrchestratorRunOutcome(
    bool Succeeded,
    bool Retryable,
    string? FailureReason,
    TurnCompletionReason? LastReason)
{
    public static OrchestratorRunOutcome Success { get; } = new(true, false, null, null);

    public static OrchestratorRunOutcome Failed(string reason, TurnCompletionReason? lastReason = null) =>
        new(false, true, reason, lastReason);

    public static OrchestratorRunOutcome Cancelled(string reason) =>
        new(false, false, reason, TurnCompletionReason.Cancelled);

    public bool IsCancelled => !Succeeded && !Retryable;
}
