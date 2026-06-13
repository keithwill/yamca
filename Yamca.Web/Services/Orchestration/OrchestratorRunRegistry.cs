using Yamca.Agent.Chat;
using Yamca.Agent.Orchestration;

namespace Yamca.Web.Services.Orchestration;

/// <summary>One orchestrated run mirrored into <see cref="ChatTurn"/>s so any browser tab can
/// render a live, read-only transcript with the same components as a real chat. A run spans
/// multiple turns (seed + continuations); <see cref="Turns"/> grows as the runner issues them.
/// Mutable agent state lives on the thread-safe <see cref="ChatTurn"/>; the scalar fields are
/// written once on start/complete (same discipline as <see cref="SubagentLiveSession"/>).</summary>
public sealed class OrchestratorLiveRun
{
    public OrchestratorLiveRun(OrchestratorRunInfo info)
    {
        RunId = info.RunId;
        CardId = info.CardId;
        CardTitle = info.CardTitle;
        ColumnId = info.ColumnId;
        ColumnDisplayName = info.ColumnDisplayName;
        Branch = info.Branch;
        WorktreePath = info.WorktreePath;
        Attempt = info.Attempt;
        StartedAt = info.StartedAt;
    }

    public string RunId { get; }
    public string CardId { get; }
    public string CardTitle { get; }
    public string ColumnId { get; }
    public string ColumnDisplayName { get; }
    public string Branch { get; }
    public string WorktreePath { get; }

    /// <summary>1-based attempt number (1 = first try, 2+ = retries).</summary>
    public int Attempt { get; }

    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public CardRunStatus Status { get; internal set; } = CardRunStatus.Running;
    public string? FailureReason { get; internal set; }

    /// <summary>The run's turns, oldest first: the seed turn plus one per continuation prompt.
    /// The list reference is only mutated under the registry's gate; UI readers should take
    /// <see cref="TurnsSnapshot"/>.</summary>
    internal List<ChatTurn> Turns { get; } = new();

    /// <summary>Cumulative token usage across the run's turns, from <see cref="UsageUpdateEvent"/>.</summary>
    public long PromptTokens { get; internal set; }
    public long CompletionTokens { get; internal set; }

    /// <summary>Set after the transcript is saved to chat history, linking the live view to the
    /// persisted document.</summary>
    public Guid? PersistedChatId { get; internal set; }

    public TimeSpan? Duration => CompletedAt is { } end ? end - StartedAt : null;
}

/// <summary>Server-wide (singleton) store of orchestrator runs, populated by the orchestrator
/// via <see cref="IOrchestratorObserver"/> and read from any circuit to render board badges and
/// live transcripts. Unlike the per-circuit <see cref="SubagentSessionRegistry"/>, this registry
/// is shared across every browser tab — the orchestrator is a server-side service, so its runs
/// belong to the process, not a circuit. Completed runs are capped (transcripts persist to chat
/// history anyway). Callbacks arrive on background continuations; all access is gate-guarded and
/// <see cref="Changed"/> subscribers must marshal to their dispatcher themselves.</summary>
public sealed class OrchestratorRunRegistry : IOrchestratorObserver
{
    /// <summary>Completed runs retained for the live viewer (oldest dropped first).</summary>
    private const int MaxCompletedRuns = 50;

    private readonly object _gate = new();
    private readonly List<OrchestratorLiveRun> _runs = new();
    private readonly Dictionary<string, OrchestratorLiveRun> _byRunId = new(StringComparer.Ordinal);

    /// <summary>Raised after any change. Raised off the Blazor dispatcher.</summary>
    public event Action? Changed;

    /// <summary>All retained runs, oldest first.</summary>
    public IReadOnlyList<OrchestratorLiveRun> Runs
    {
        get { lock (_gate) return _runs.ToArray(); }
    }

    /// <summary>The in-flight run for a card, if any (drives the board's running badge).</summary>
    public OrchestratorLiveRun? ActiveRunForCard(string cardId)
    {
        lock (_gate)
            return _runs.LastOrDefault(r => r.CardId == cardId && r.Status == CardRunStatus.Running);
    }

    /// <summary>The most recent run for a card, running or not (drives badge tooltips/dialog links).</summary>
    public OrchestratorLiveRun? LatestRunForCard(string cardId)
    {
        lock (_gate) return _runs.LastOrDefault(r => r.CardId == cardId);
    }

    /// <summary>Snapshot of a run's turns for rendering (the list itself is mutated under the gate).</summary>
    public IReadOnlyList<ChatTurn> TurnsSnapshot(OrchestratorLiveRun run)
    {
        lock (_gate) return run.Turns.ToArray();
    }

    /// <summary>Aggregate token usage across all retained runs.</summary>
    public (long Prompt, long Completion) AggregateTokens
    {
        get
        {
            lock (_gate)
            {
                long p = 0, c = 0;
                foreach (var r in _runs) { p += r.PromptTokens; c += r.CompletionTokens; }
                return (p, c);
            }
        }
    }

    /// <summary>Drop completed runs (running ones are kept).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var keep = _runs.Where(r => r.Status == CardRunStatus.Running).ToList();
            _runs.Clear();
            _runs.AddRange(keep);
            _byRunId.Clear();
            foreach (var r in keep) _byRunId[r.RunId] = r;
        }
        Changed?.Invoke();
    }

    /// <summary>Link a completed run to its persisted chat document.</summary>
    public void SetPersistedChatId(string runId, Guid chatId)
    {
        lock (_gate)
        {
            if (_byRunId.TryGetValue(runId, out var run))
                run.PersistedChatId = chatId;
        }
        Changed?.Invoke();
    }

    public void OnRunStarted(OrchestratorRunInfo info)
    {
        var run = new OrchestratorLiveRun(info);
        lock (_gate)
        {
            _runs.Add(run);
            _byRunId[info.RunId] = run;
            TrimCompletedLocked();
        }
        Changed?.Invoke();
    }

    public void OnTurnStarted(string runId, string userMessage)
    {
        lock (_gate)
        {
            if (_byRunId.TryGetValue(runId, out var run))
            {
                // The previous turn (if any) is over once a continuation starts.
                if (run.Turns.Count > 0)
                {
                    run.Turns[^1].IsRunning = false;
                    run.Turns[^1].Activity = TurnActivity.Idle;
                }
                run.Turns.Add(new ChatTurn(userMessage));
            }
        }
        Changed?.Invoke();
    }

    public void OnRunEvent(string runId, ChatStreamEvent ev)
    {
        OrchestratorLiveRun? run;
        ChatTurn? turn;
        lock (_gate)
        {
            run = _byRunId.GetValueOrDefault(runId);
            turn = run?.Turns.Count > 0 ? run.Turns[^1] : null;
        }
        if (run is null || turn is null) return;

        if (ev is UsageUpdateEvent usage)
        {
            // Each usage report covers one LLM request (one round-trip of the tool loop), so
            // summing them yields the run's cumulative server-side token cost. Usage events are
            // bookkeeping only — not forwarded to the turn applier, same as ChatViewModel.Apply.
            lock (_gate)
            {
                run.PromptTokens += usage.PromptTokens;
                run.CompletionTokens += usage.CompletionTokens;
            }
            Changed?.Invoke();
            return;
        }

        // ChatTurn is internally thread-safe; no need to hold the gate while applying.
        ChatTurnApplier.Apply(turn, ev);
        Changed?.Invoke();
    }

    public void OnRunCompleted(string runId, OrchestratorRunOutcome outcome)
    {
        OrchestratorLiveRun? run;
        lock (_gate)
        {
            run = _byRunId.GetValueOrDefault(runId);
            if (run is not null)
            {
                run.Status = outcome.Succeeded
                    ? CardRunStatus.Succeeded
                    : outcome.IsCancelled ? CardRunStatus.Cancelled : CardRunStatus.Failed;
                run.FailureReason = outcome.FailureReason;
                run.CompletedAt = DateTimeOffset.Now;
                foreach (var turn in run.Turns)
                {
                    turn.IsRunning = false;
                    turn.Activity = TurnActivity.Idle;
                }
            }
        }
        if (run is not null) Changed?.Invoke();
    }

    // Must be called with _gate held.
    private void TrimCompletedLocked()
    {
        var completed = _runs.Count(r => r.Status != CardRunStatus.Running);
        if (completed <= MaxCompletedRuns) return;
        foreach (var stale in _runs.Where(r => r.Status != CardRunStatus.Running)
                     .Take(completed - MaxCompletedRuns).ToList())
        {
            _runs.Remove(stale);
            _byRunId.Remove(stale.RunId);
        }
    }
}
