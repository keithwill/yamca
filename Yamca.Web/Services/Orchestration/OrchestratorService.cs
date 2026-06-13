using System.Threading.Channels;
using Yamca.Agent.Board;
using Yamca.Agent.Chat;
using Yamca.Agent.Chat.Persistence;
using Yamca.Agent.Orchestration;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Web.Services.Orchestration;

/// <summary>
/// The board orchestrator: a server-side engine that, when enabled, autonomously dispatches
/// cards in enabled work columns to headless agent runs (<see cref="OrchestratorCardRunner"/>),
/// Symphony-style. One long-lived loop (<see cref="RunLoopAsync"/>, started by
/// <see cref="OrchestratorHost"/>) is the single writer of all orchestration state: run tasks
/// report back through a channel, the UI reads immutable snapshots, and Enable/Disable just
/// flip a flag and wake the loop.
///
/// Each tick: drain run completions → reconcile active runs against a fresh board read →
/// re-resolve settings from disk (hot reload: circuit edits persist to project.json
/// immediately, so the next tick sees them) → validate (failure skips dispatch but never
/// reconciliation, keeping the last known good config) → select candidates by priority →
/// dispatch into free concurrency slots. Failures retry with exponential backoff up to the
/// configured attempts, then park the card until the user moves/edits it, re-enables the
/// orchestrator, or asks for a retry.
/// </summary>
public sealed class OrchestratorService : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWorkspace _rootWorkspace;
    private readonly BoardStore _boardStore;
    private readonly CardWorktreeProvisioner _provisioner;
    private readonly EndpointClientFactory _clientFactory;
    private readonly OrchestratorRunRegistry _registry;
    private readonly OrchestratorCardRunner _runner;
    private readonly ChatStore _chatStore;
    private readonly ILogger<OrchestratorService> _log;

    // --- loop-owned state (mutated only inside RunLoopAsync / ApplyMessage) ---------------
    private readonly Dictionary<string, CardOrchestrationState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);

    // --- cross-thread communication --------------------------------------------------------
    private readonly Channel<LoopMessage> _messages = Channel.CreateUnbounded<LoopMessage>();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _enabled;
    private volatile string? _lastValidationError;
    private IReadOnlyDictionary<string, CardOrchestrationState> _statesSnapshot =
        new Dictionary<string, CardOrchestrationState>(StringComparer.OrdinalIgnoreCase);

    // Last successfully validated config, kept so a transiently broken edit (or unreadable
    // file) degrades to "skip dispatch" instead of crashing the cadence.
    private OrchestratorSettings _lastGoodSettings = OrchestratorSettings.Default;

    private sealed record ActiveRun(string CardId, string RunId, CancellationTokenSource Cts, Task Task);

    private abstract record LoopMessage;
    private sealed record RunStarted(string CardId, string RunId) : LoopMessage;
    private sealed record RunCompleted(string CardId, string RunId, OrchestratorRunOutcome Outcome) : LoopMessage;

    public OrchestratorService(
        IServiceScopeFactory scopes,
        IWorkspace rootWorkspace,
        BoardStore boardStore,
        CardWorktreeProvisioner provisioner,
        EndpointClientFactory clientFactory,
        OrchestratorRunRegistry registry,
        OrchestratorStartupOptions startup,
        ILogger<OrchestratorService> log)
    {
        _scopes = scopes;
        _rootWorkspace = rootWorkspace;
        _boardStore = boardStore;
        _provisioner = provisioner;
        _clientFactory = clientFactory;
        _registry = registry;
        _runner = new OrchestratorCardRunner(boardStore);
        // The orchestrator persists run transcripts itself; like every ChatStore consumer it
        // anchors on the root workspace. Its internal lock is separate from the per-circuit
        // instances', but index.json writes are atomic and the index self-heals by rescanning,
        // so cross-instance races degrade to a transiently stale list at worst.
        _chatStore = new ChatStore(rootWorkspace);
        _log = log;
        _enabled = startup.StartEnabled;
    }

    /// <summary>Whether dispatch is active. Runtime-only: never persisted, starts false unless
    /// <see cref="OrchestratorStartupOptions.StartEnabled"/> says otherwise.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Errors from the most recent dispatch-config validation, or null when valid.</summary>
    public string? LastValidationError => _lastValidationError;

    public int RunningCount
    {
        get { lock (_activeRuns) return _activeRuns.Count; }
    }

    /// <summary>Immutable per-card orchestration state, keyed by card id (drives board badges).</summary>
    public IReadOnlyDictionary<string, CardOrchestrationState> CardStates => _statesSnapshot;

    /// <summary>Raised (off the Blazor dispatcher) whenever enable state, validation, or the
    /// per-card state snapshot changes.</summary>
    public event Action? StateChanged;

    /// <summary>Turn dispatch on. Clears parked and retry state — re-enabling is the operator's
    /// "try everything again" gesture.</summary>
    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _messages.Writer.TryWrite(new ClearStaleState());
        Wake();
        RaiseStateChanged();
    }

    /// <summary>Turn dispatch off and cancel every in-flight run. Parked/retry markers are kept
    /// (their reasons stay visible) until the next <see cref="Enable"/> clears them.</summary>
    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        CancelAllRuns();
        Wake();
        RaiseStateChanged();
    }

    /// <summary>Drop a card's parked/retry marker so the next tick can dispatch it again.</summary>
    public void RetryNow(string cardId)
    {
        _messages.Writer.TryWrite(new ForgetCard(cardId));
        Wake();
    }

    private sealed record ClearStaleState : LoopMessage;
    private sealed record ForgetCard(string CardId) : LoopMessage;

    // ------------------------------------------------------------------------------------
    // The loop (single writer)
    // ------------------------------------------------------------------------------------

    /// <summary>The orchestrator's poll loop. Runs for the process lifetime — reconciliation
    /// and message draining continue even while disabled; only dispatch is gated.</summary>
    internal async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "orchestrator tick failed");
            }

            try
            {
                var interval = TimeSpan.FromSeconds(_lastGoodSettings.PollIntervalSeconds);
                var delay = Task.Delay(interval, ct);
                var wake = _wake.WaitAsync(ct);
                var message = _messages.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(delay, wake, message).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var changed = DrainMessages();

        var board = await _boardStore.ReadAsync(ct).ConfigureAwait(false);

        // Settings are re-read from disk every tick so circuit edits apply to future dispatch
        // without a restart. Validation failure keeps the last good config and skips dispatch;
        // reconciliation below still runs.
        var (settings, endpoints, valid) = ResolveSettings(board);

        changed |= Reconcile(board, settings);

        if (_enabled && valid)
            changed |= Dispatch(board, settings, endpoints!, ct);

        if (changed)
        {
            PublishSnapshot();
            RaiseStateChanged();
        }
    }

    private bool DrainMessages()
    {
        var changed = false;
        while (_messages.Reader.TryRead(out var message))
        {
            changed |= ApplyMessage(message);
        }
        return changed;
    }

    private bool ApplyMessage(LoopMessage message)
    {
        switch (message)
        {
            case RunStarted(var cardId, var runId):
                if (_states.TryGetValue(cardId, out var queued) && queued.ActiveRunId == runId)
                    _states[cardId] = queued with { Status = CardRunStatus.Running };
                return true;

            case RunCompleted(var cardId, var runId, var outcome):
                lock (_activeRuns)
                {
                    if (_activeRuns.Remove(runId, out var finished))
                        finished.Cts.Dispose();
                }
                if (!_states.TryGetValue(cardId, out var state) || state.ActiveRunId != runId)
                    return true; // entry already released (e.g. ForgetCard) — nothing to update

                if (outcome.Succeeded || outcome.IsCancelled)
                {
                    // Released: success moves on, cancellation returns the card to the pool
                    // (the next tick re-evaluates whether it is still a candidate).
                    _states.Remove(cardId);
                }
                else
                {
                    var attempts = state.Attempts + 1;
                    if (RetryPolicy.ShouldPark(attempts, _lastGoodSettings.RetryMaxAttempts))
                    {
                        _states[cardId] = state with
                        {
                            Status = CardRunStatus.Parked,
                            Attempts = attempts,
                            NextAttemptUtc = null,
                            FailureReason = outcome.FailureReason,
                            ActiveRunId = null,
                        };
                        _log.LogWarning("orchestrator parked card {CardId} after {Attempts} attempts: {Reason}",
                            cardId, attempts, outcome.FailureReason);
                    }
                    else
                    {
                        var delay = RetryPolicy.DelayFor(
                            attempts,
                            TimeSpan.FromSeconds(_lastGoodSettings.RetryBaseDelaySeconds),
                            TimeSpan.FromSeconds(_lastGoodSettings.RetryMaxDelaySeconds));
                        _states[cardId] = state with
                        {
                            Status = CardRunStatus.Retrying,
                            Attempts = attempts,
                            NextAttemptUtc = DateTimeOffset.UtcNow + delay,
                            FailureReason = outcome.FailureReason,
                            ActiveRunId = null,
                        };
                        _log.LogInformation(
                            "orchestrator will retry card {CardId} in {Delay} (attempt {Attempts}): {Reason}",
                            cardId, delay, attempts, outcome.FailureReason);
                    }
                }
                return true;

            case ClearStaleState:
                // Re-enable gesture: forget parked/retry markers so everything is eligible again.
                foreach (var key in _states.Where(kv =>
                             kv.Value.Status is CardRunStatus.Parked or CardRunStatus.Retrying)
                             .Select(kv => kv.Key).ToList())
                    _states.Remove(key);
                return true;

            case ForgetCard(var id):
                if (_states.TryGetValue(id, out var s)
                    && s.Status is CardRunStatus.Parked or CardRunStatus.Retrying)
                    _states.Remove(id);
                return true;

            default:
                return false;
        }
    }

    private (OrchestratorSettings Settings, EndpointsSettings? Endpoints, bool Valid) ResolveSettings(
        BoardSnapshot board)
    {
        OrchestratorSettings settings;
        EndpointsSettings endpoints;
        try
        {
            using var scope = _scopes.CreateScope();
            var hydrator = scope.ServiceProvider.GetRequiredService<SettingsHydrator>();
            hydrator.HydrateAsync().GetAwaiter().GetResult(); // synchronous under the hood
            var session = scope.ServiceProvider.GetRequiredService<ISessionSettings>();
            settings = session.Orchestrator;
            endpoints = session.Endpoints;
        }
        catch (Exception ex)
        {
            _lastValidationError = $"Could not load settings: {ex.Message}";
            return (_lastGoodSettings, null, false);
        }

        var result = OrchestratorSettingsValidator.Validate(settings, endpoints, board);
        if (!result.IsValid)
        {
            _lastValidationError = string.Join(" ", result.Errors);
            return (settings, null, false);
        }

        _lastValidationError = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;
        _lastGoodSettings = settings;
        return (settings, endpoints, true);
    }

    /// <summary>Check every tracked card against the fresh board: cancel runs whose card was
    /// moved or deleted (or whose column was disabled), and drop retry/park markers once the
    /// user has touched the card (moving or deleting it is the un-park gesture).</summary>
    private bool Reconcile(BoardSnapshot board, OrchestratorSettings settings)
    {
        var changed = false;
        foreach (var (cardId, state) in _states.ToList())
        {
            if (state.Status is CardRunStatus.Queued or CardRunStatus.Running)
            {
                var action = OrchestratorPlanner.Reconcile(state, board, settings);
                if (action == OrchestratorPlanner.ReconcileAction.None) continue;

                // Cancel the run; its finalizer re-reads the board and classifies the outcome
                // (moved/deleted → success, otherwise cancelled), which flows back through the
                // completion channel and releases the entry.
                if (state.ActiveRunId is { } runId)
                {
                    CancelRun(runId);
                    _log.LogInformation("orchestrator cancelling run for card {CardId}: {Action}", cardId, action);
                }
                changed = true;
            }
            else if (state.Status is CardRunStatus.Retrying or CardRunStatus.Parked)
            {
                var card = board.FindCard(cardId);
                if (card is null
                    || !string.Equals(card.ColumnId, state.ColumnId, StringComparison.OrdinalIgnoreCase))
                {
                    _states.Remove(cardId);
                    changed = true;
                }
            }
        }
        return changed;
    }

    private bool Dispatch(
        BoardSnapshot board,
        OrchestratorSettings settings,
        EndpointsSettings endpoints,
        CancellationToken loopCt)
    {
        var candidates = OrchestratorPlanner.SelectCandidates(board, settings, _states, DateTimeOffset.UtcNow);
        if (candidates.Count == 0) return false;

        var runningPerColumn = _states.Values
            .Where(s => s.Status is CardRunStatus.Queued or CardRunStatus.Running)
            .GroupBy(s => s.ColumnId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var runningTotal = runningPerColumn.Values.Sum();

        var dispatchable = OrchestratorPlanner.TakeDispatchable(candidates, runningPerColumn, runningTotal, settings);
        if (dispatchable.Count == 0) return false;

        foreach (var (card, column) in dispatchable)
        {
            var runId = Guid.NewGuid().ToString("n");
            var attempts = _states.TryGetValue(card.Id, out var prior) ? prior.Attempts : 0;
            _states[card.Id] = new CardOrchestrationState(
                card.Id, column.Id, CardRunStatus.Queued, attempts,
                NextAttemptUtc: null, FailureReason: null, ActiveRunId: runId);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
            var task = Task.Run(
                () => DispatchRunAsync(card, column, settings, endpoints, runId, attempts + 1, cts.Token),
                CancellationToken.None);
            lock (_activeRuns) _activeRuns[runId] = new ActiveRun(card.Id, runId, cts, task);

            _log.LogInformation("orchestrator dispatching card {CardId} ({Title}) in {Column}, attempt {Attempt}",
                card.Id, card.Title, column.DisplayName, attempts + 1);
        }

        return true;
    }

    // ------------------------------------------------------------------------------------
    // Per-run dispatch (runs on its own task; reports back via the message channel)
    // ------------------------------------------------------------------------------------

    private async Task DispatchRunAsync(
        BoardCard card,
        BoardColumn column,
        OrchestratorSettings settings,
        EndpointsSettings endpoints,
        string runId,
        int attempt,
        CancellationToken ct)
    {
        var outcome = OrchestratorRunOutcome.Failed("run did not start");
        var started = false;
        try
        {
            // 1. Branch + worktree, exactly like the interactive Run Step flow.
            var branch = !string.IsNullOrWhiteSpace(card.Branch)
                ? card.Branch
                : BoardService.PresumptiveBranch(card.Id, card.Title);
            if (!string.Equals(card.Branch, branch, StringComparison.Ordinal))
            {
                if (await _provisioner.LockCardToBranchAsync(card, branch, ct).ConfigureAwait(false) is { } lockError)
                {
                    outcome = OrchestratorRunOutcome.Failed(lockError);
                    return;
                }
            }

            var provision = await _provisioner.ResolveWorktreeForBranchAsync(branch, ct).ConfigureAwait(false);
            if (provision.Worktree is null)
            {
                outcome = OrchestratorRunOutcome.Failed(provision.Error ?? "could not provision worktree");
                return;
            }

            var instructions = column.Instructions;

            // 2. Endpoint (re-validated here — settings may have changed since the tick).
            var endpoint = settings.EndpointId is Guid id ? endpoints.FindById(id) : endpoints.Default;
            if (endpoint is null)
            {
                outcome = OrchestratorRunOutcome.Failed("the configured orchestrator endpoint no longer exists");
                return;
            }
            var client = _clientFactory.CreateCompletionClient(endpoint);

            // 3. Per-run DI scope. Hydrate its settings *before* enumerating tools — scoped
            // tools (execute_*, git, lookup/call) read the scope's ISessionSettings. The scope
            // lives for the whole run so those tools stay valid.
            using var scope = _scopes.CreateScope();
            var hydrator = scope.ServiceProvider.GetRequiredService<SettingsHydrator>();
            await hydrator.HydrateAsync().ConfigureAwait(false);
            var session = scope.ServiceProvider.GetRequiredService<ISessionSettings>();
            var tools = scope.ServiceProvider.GetRequiredService<IToolRegistry>().Tools.ToList<ITool>();

            var worktreeWorkspace = new WorkspaceImpl(provision.Worktree.WorktreePath);

            _registry.OnRunStarted(new OrchestratorRunInfo(
                runId, card.Id, card.Title, column.Id, column.DisplayName,
                branch, provision.Worktree.WorktreePath, attempt,
                BoardPrompts.BuildSeedPrompt(card, column, instructions), DateTimeOffset.Now));
            started = true;
            _messages.Writer.TryWrite(new RunStarted(card.Id, runId));
            Wake();

            // 4. Drive the headless run.
            var request = new OrchestratorRunRequest(
                card, column, instructions, worktreeWorkspace, client, tools,
                settings, session.MaxToolIterations, runId, _registry);
            var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
            outcome = result.Outcome;

            // 5. Persist the transcript (failed runs too — they're the ones worth reviewing).
            PersistRun(runId, card, column, endpoint, provision.Worktree, result);
        }
        catch (OperationCanceledException)
        {
            outcome = OrchestratorRunOutcome.Cancelled("run was cancelled");
        }
        catch (Exception ex)
        {
            outcome = OrchestratorRunOutcome.Failed($"dispatch failed: {ex.Message}");
            _log.LogError(ex, "orchestrator dispatch for card {CardId} failed", card.Id);
        }
        finally
        {
            if (started) _registry.OnRunCompleted(runId, outcome);
            _messages.Writer.TryWrite(new RunCompleted(card.Id, runId, outcome));
            Wake();
        }
    }

    private void PersistRun(
        string runId,
        BoardCard card,
        BoardColumn column,
        EndpointSettings endpoint,
        Yamca.Agent.Git.WorktreeInfo worktree,
        OrchestratorRunResult result)
    {
        try
        {
            var live = _registry.Runs.FirstOrDefault(r => r.RunId == runId);
            var turns = live is null ? Array.Empty<ChatTurn>() : _registry.TurnsSnapshot(live);
            if (turns.Count == 0) return;

            var doc = new PersistedChat
            {
                Id = Guid.NewGuid(),
                Title = $"Orchestrator: {card.Title} — {column.DisplayName}",
                CreatedUtc = live?.StartedAt.ToUniversalTime() ?? DateTimeOffset.UtcNow,
                Endpoint = new PersistedEndpoint(endpoint.Id, endpoint.Name, endpoint.BaseUrl, endpoint.Model),
                Worktree = worktree,
                WorkspaceRootPath = worktree.WorktreePath,
                Messages = result.Messages.ToList(),
                Turns = turns.Select(ChatTurnPersistence.ToPersistedTurn).ToList(),
            };
            _chatStore.Save(doc);
            _registry.SetPersistedChatId(runId, doc.Id);
        }
        catch (Exception ex)
        {
            // Persistence must never turn a finished run into a failure.
            _log.LogWarning(ex, "orchestrator could not persist transcript for card {CardId}", card.Id);
        }
    }

    // ------------------------------------------------------------------------------------
    // Shutdown / plumbing
    // ------------------------------------------------------------------------------------

    /// <summary>Cancel all in-flight runs and wait for them to finish (bounded by
    /// <paramref name="ct"/>, the host's shutdown token).</summary>
    internal async Task ShutdownAsync(CancellationToken ct)
    {
        CancelAllRuns();
        Task[] pending;
        lock (_activeRuns) pending = _activeRuns.Values.Select(r => r.Task).ToArray();
        if (pending.Length == 0) return;
        try
        {
            await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown is best-effort; individual run failures were already routed
            // through the completion channel.
        }
    }

    private void CancelAllRuns()
    {
        List<ActiveRun> runs;
        lock (_activeRuns) runs = _activeRuns.Values.ToList();
        foreach (var run in runs)
        {
            try { run.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }
    }

    private void CancelRun(string runId)
    {
        ActiveRun? run;
        lock (_activeRuns) run = _activeRuns.GetValueOrDefault(runId);
        if (run is null) return;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    private void PublishSnapshot()
    {
        _statesSnapshot = new Dictionary<string, CardOrchestrationState>(_states, StringComparer.OrdinalIgnoreCase);
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        CancelAllRuns();
        _wake.Dispose();
    }
}
