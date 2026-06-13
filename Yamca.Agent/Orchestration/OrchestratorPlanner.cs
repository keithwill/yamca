using Yamca.Agent.Board;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Orchestration;

/// <summary>Pure planning logic for the orchestrator's poll tick: candidate selection,
/// concurrency slot math, and reconciliation verdicts. No I/O and no mutation — the
/// orchestrator loop owns all state changes.</summary>
public static class OrchestratorPlanner
{
    /// <summary>What the reconciler should do about an active (queued/running) entry given a
    /// fresh board snapshot.</summary>
    public enum ReconcileAction
    {
        /// <summary>Card is still in its source column and the column is still enabled.</summary>
        None,
        /// <summary>Card left its source column (agent or user moved it) — cancel the run; its
        /// finalizer classifies the outcome as success via its own board re-read.</summary>
        CompleteAsMoved,
        /// <summary>Card no longer exists — cancel and release.</summary>
        CancelDeleted,
        /// <summary>Card's column was removed from the enabled set mid-run — cancel and release.</summary>
        CancelColumnDisabled,
    }

    /// <summary>Cards eligible for dispatch: in an enabled column, not claimed (queued/running),
    /// not parked, and not retrying before their due time. Sorted by priority high → normal →
    /// low, then numeric id ascending (oldest first), across all enabled columns — the same
    /// ordering the board displays within a column.</summary>
    public static IReadOnlyList<(BoardCard Card, BoardColumn Column)> SelectCandidates(
        BoardSnapshot board,
        OrchestratorSettings settings,
        IReadOnlyDictionary<string, CardOrchestrationState> states,
        DateTimeOffset nowUtc)
    {
        var enabled = new HashSet<string>(settings.EnabledColumns, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(BoardCard Card, BoardColumn Column)>();

        foreach (var column in board.Columns)
        {
            if (!enabled.Contains(column.Id)) continue;
            foreach (var card in column.Cards)
            {
                if (states.TryGetValue(card.Id, out var state))
                {
                    if (state.Status is CardRunStatus.Queued or CardRunStatus.Running or CardRunStatus.Parked)
                        continue;
                    if (state.Status is CardRunStatus.Retrying && state.NextAttemptUtc is { } due && due > nowUtc)
                        continue;
                }
                candidates.Add((card, column));
            }
        }

        candidates.Sort(static (a, b) => BoardService.CompareCards(a.Card, b.Card));
        return candidates;
    }

    /// <summary>Take as many candidates (in order) as the global and optional per-column
    /// concurrency limits allow.</summary>
    public static IReadOnlyList<(BoardCard Card, BoardColumn Column)> TakeDispatchable(
        IReadOnlyList<(BoardCard Card, BoardColumn Column)> candidates,
        IReadOnlyDictionary<string, int> runningPerColumn,
        int runningTotal,
        OrchestratorSettings settings)
    {
        var globalSlots = Math.Max(settings.MaxConcurrentRuns - runningTotal, 0);
        if (globalSlots == 0) return Array.Empty<(BoardCard, BoardColumn)>();

        var perColumn = new Dictionary<string, int>(runningPerColumn, StringComparer.OrdinalIgnoreCase);
        var taken = new List<(BoardCard, BoardColumn)>();

        foreach (var (card, column) in candidates)
        {
            if (taken.Count >= globalSlots) break;
            if (settings.MaxConcurrentRunsPerColumn is int cap)
            {
                var inColumn = perColumn.GetValueOrDefault(column.Id);
                if (inColumn >= cap) continue;
                perColumn[column.Id] = inColumn + 1;
            }
            taken.Add((card, column));
        }

        return taken;
    }

    /// <summary>Reconcile one active (queued/running) entry against a fresh board snapshot
    /// and the current enabled-column set.</summary>
    public static ReconcileAction Reconcile(
        CardOrchestrationState state,
        BoardSnapshot board,
        OrchestratorSettings settings)
    {
        var card = board.FindCard(state.CardId);
        if (card is null)
            return ReconcileAction.CancelDeleted;

        if (!string.Equals(card.ColumnId, state.ColumnId, StringComparison.OrdinalIgnoreCase))
            return ReconcileAction.CompleteAsMoved;

        if (!settings.EnabledColumns.Contains(state.ColumnId, StringComparer.OrdinalIgnoreCase))
            return ReconcileAction.CancelColumnDisabled;

        return ReconcileAction.None;
    }
}
