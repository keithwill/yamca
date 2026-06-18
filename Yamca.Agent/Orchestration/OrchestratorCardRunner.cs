using Yamca.Agent.Board;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Orchestration;

/// <summary>Everything one orchestrated run needs, assembled by the dispatcher.
/// <see cref="SourceTools"/> is the full tool set from the run's DI scope; the runner filters
/// it to the settings' allowed list. <see cref="SessionMaxToolIterations"/> is the session-wide
/// iteration cap, used when the settings don't override it.</summary>
public sealed record OrchestratorRunRequest(
    BoardCard Card,
    BoardColumn Column,
    string? Instructions,
    IWorkspace WorktreeWorkspace,
    IChatCompletionClient Client,
    IReadOnlyList<ITool> SourceTools,
    OrchestratorSettings Settings,
    int SessionMaxToolIterations,
    string RunId,
    IOrchestratorObserver Observer,
    Guid EndpointId,
    string EndpointName,
    string Model,
    string EndpointBaseUrl,
    bool RecordMetrics);

/// <summary>Outcome plus the final LLM message log (for transcript persistence; safe to read
/// once the run has stopped) and how many turns the run consumed.</summary>
public sealed record OrchestratorRunResult(
    OrchestratorRunOutcome Outcome,
    IReadOnlyList<ChatMessage> Messages,
    int TurnCount);

/// <summary>
/// Drives one headless agent run against a board card, templated on
/// <see cref="SubagentRunner"/>: a curated auto-allowed tool set, no approval prompts, and a
/// bounded multi-turn loop. The run succeeds when the card leaves its source column — the
/// authoritative check is a board re-read after every turn; spotting a successful
/// <c>board_move_card</c> tool result mid-stream is just an optimization to stop consuming
/// the turn early. Watchdogs cancel a turn that stalls (no stream events) or exceeds its
/// wall-clock budget; both are retryable failures. Stateless and reentrant — all per-run
/// state lives in locals.
/// </summary>
public sealed class OrchestratorCardRunner
{
    private const int FailureTailChars = 600;

    private readonly BoardStore _boardStore;
    private readonly Yamca.Agent.Metrics.ITurnMetricSink? _metrics;

    public OrchestratorCardRunner(BoardStore boardStore, Yamca.Agent.Metrics.ITurnMetricSink? metrics = null)
    {
        _boardStore = boardStore;
        _metrics = metrics;
    }

    public async Task<OrchestratorRunResult> RunAsync(OrchestratorRunRequest req, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Tool set: the scope's tools filtered to the allowed list. subagent_run and loop are
        // always excluded — an orchestrated run must not fan out further agents.
        var allowed = new HashSet<string>(req.Settings.AllowedTools, StringComparer.Ordinal);
        var childTools = req.SourceTools
            .Where(t => allowed.Contains(t.Name)
                && t.Name != SubagentRunTool.ToolName
                && t.Name != LoopTool.ToolName)
            .ToList();

        var childRegistry = new ToolRegistry(childTools);
        var permissions = new SubagentPermissionResolver(childRegistry, req.Settings.RestrictToWorkspace);
        var availability = new SubagentAvailabilityResolver();

        var session = BuildSession(req, childTools, permissions);

        var loop = new AgentLoop(
            session,
            req.Client,
            childRegistry,
            permissions,
            availability,
            new NoopApprovalCoordinator(),
            new NoopPermissionStore(),
            req.WorktreeWorkspace,
            new LoadedToolSet(),
            new AgentLoopOptions
            {
                MaxIterations = req.Settings.MaxToolIterationsPerTurn ?? req.SessionMaxToolIterations,
                OwnerId = req.RunId,
                EndpointId = req.EndpointId,
                EndpointName = req.EndpointName,
                Model = req.Model,
                EndpointBaseUrl = req.EndpointBaseUrl,
                RecordMetrics = req.RecordMetrics,
            },
            isYoloEnabled: static () => true,
            metrics: _metrics);

        var (outcome, turns) = await DriveAsync(req, loop, cancellationToken).ConfigureAwait(false);
        return new OrchestratorRunResult(outcome, session.Messages.ToList(), turns);
    }

    private async Task<(OrchestratorRunOutcome Outcome, int Turns)> DriveAsync(
        OrchestratorRunRequest req, AgentLoop loop, CancellationToken cancellationToken)
    {
        var seedPrompt = BoardPrompts.BuildSeedPrompt(req.Card, req.Column, req.Instructions);
        var stallTimeout = TimeSpan.FromSeconds(req.Settings.StallTimeoutSeconds);
        var turnTimeout = TimeSpan.FromSeconds(req.Settings.TurnTimeoutSeconds);

        var turns = 0;
        var lastAssistant = "";
        var continueSameTurn = false;
        TurnCompletionReason? lastReason = null;

        // Drives one turn (or an iteration-cap resume) under the watchdogs. Returns true when
        // the card was observed to move via the tool-result fast path.
        async Task<bool> DriveTurnAsync(
            Func<CancellationToken, IAsyncEnumerable<ChatStreamEvent>> run, TurnWatchdog watchdog)
        {
            await foreach (var ev in run(watchdog.Token).ConfigureAwait(false))
            {
                watchdog.Pet();
                req.Observer.OnRunEvent(req.RunId, ev);
                switch (ev)
                {
                    case ToolCallResultEvent { ToolName: "board_move_card", IsError: false }:
                        // The agent moved the card — the run's goal. Stop consuming the turn
                        // rather than letting the model burn further iterations.
                        return true;
                    case AssistantMessageEvent a when !string.IsNullOrWhiteSpace(a.Content):
                        lastAssistant = a.Content;
                        break;
                    case TurnCompleteEvent c:
                        lastReason = c.Reason;
                        break;
                }
            }
            return false;
        }

        while (true)
        {
            // Each drive — seed, continuation, or iteration-cap resume — consumes a full
            // tool-iteration budget, so each counts against MaxTurnsPerRun.
            turns++;
            lastReason = null;
            using var watchdog = new TurnWatchdog(cancellationToken, stallTimeout, turnTimeout);

            bool movedFastPath;
            try
            {
                if (turns == 1)
                {
                    req.Observer.OnTurnStarted(req.RunId, seedPrompt);
                    movedFastPath = await DriveTurnAsync(
                        ct => loop.RunTurnAsync(seedPrompt, cancellationToken: ct), watchdog).ConfigureAwait(false);
                }
                else if (continueSameTurn)
                {
                    // Resume the same logical turn: the log already ends with tool results the
                    // model hasn't answered. No OnTurnStarted — events keep applying to the
                    // current transcript turn.
                    movedFastPath = await DriveTurnAsync(loop.ContinueTurnAsync, watchdog).ConfigureAwait(false);
                }
                else
                {
                    req.Observer.OnTurnStarted(req.RunId, OrchestratorPrompts.ContinuationPrompt);
                    movedFastPath = await DriveTurnAsync(
                        ct => loop.RunTurnAsync(OrchestratorPrompts.ContinuationPrompt, cancellationToken: ct),
                        watchdog).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return (await ClassifyCancellationAsync(req, watchdog).ConfigureAwait(false), turns);
            }
            catch (Exception ex)
            {
                // Endpoint/network/etc. failures are retryable — backoff handles flapping servers.
                return (OrchestratorRunOutcome.Failed($"run attempt failed: {ex.Message}", lastReason), turns);
            }

            // Authoritative success check: did the card leave its source column?
            if (movedFastPath || await CardMovedAsync(req).ConfigureAwait(false))
                return (OrchestratorRunOutcome.Success, turns);

            // A watchdog can cancel between events without an OperationCanceledException
            // surfacing (the loop reports a Cancelled turn instead) — classify that the same way.
            if (lastReason == TurnCompletionReason.Cancelled || watchdog.Token.IsCancellationRequested)
                return (await ClassifyCancellationAsync(req, watchdog).ConfigureAwait(false), turns);

            if (turns >= req.Settings.MaxTurnsPerRun)
                return (OrchestratorRunOutcome.Failed(TurnsExhaustedMessage(req, lastAssistant), lastReason), turns);

            continueSameTurn = lastReason == TurnCompletionReason.MaxIterationsReached;
        }
    }

    private async Task<OrchestratorRunOutcome> ClassifyCancellationAsync(
        OrchestratorRunRequest req, TurnWatchdog watchdog)
    {
        if (watchdog.StallFired)
            return OrchestratorRunOutcome.Failed(
                $"stalled: no model events for {req.Settings.StallTimeoutSeconds}s", TurnCompletionReason.Cancelled);
        if (watchdog.DeadlineFired)
            return OrchestratorRunOutcome.Failed(
                $"turn timed out after {req.Settings.TurnTimeoutSeconds}s", TurnCompletionReason.Cancelled);

        // External cancellation (reconciler, disable, shutdown). One last board read decides
        // whether the goal was reached anyway (e.g. the agent moved the card and was then
        // cancelled while lingering, or the user moved it mid-run).
        if (await CardMovedAsync(req).ConfigureAwait(false))
            return OrchestratorRunOutcome.Success;
        return OrchestratorRunOutcome.Cancelled("run was cancelled");
    }

    private async Task<bool> CardMovedAsync(OrchestratorRunRequest req)
    {
        var snapshot = await _boardStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var card = snapshot.FindCard(req.Card.Id);
        return card is null
            || !string.Equals(card.ColumnId, req.Column.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static string TurnsExhaustedMessage(OrchestratorRunRequest req, string lastAssistant)
    {
        var message = $"did not move the card within {req.Settings.MaxTurnsPerRun} turns";
        if (!string.IsNullOrWhiteSpace(lastAssistant))
        {
            var tail = lastAssistant.Trim();
            if (tail.Length > FailureTailChars) tail = tail[..FailureTailChars] + "…";
            message += $"; its last message was: {tail}";
        }
        return message;
    }

    private ChatSession BuildSession(
        OrchestratorRunRequest req,
        IReadOnlyList<ITool> childTools,
        SubagentPermissionResolver permissions)
    {
        // Let tools contribute their session-start state (e.g. the registered-scripts list),
        // the same way subagent and parent chat sessions are built.
        var instructions = new List<string>();
        foreach (var tool in childTools)
        {
            var ctx = new ToolContext(req.WorktreeWorkspace, permissions.RestrictToWorkspace(tool.Name), ownerId: req.RunId);
            var contribution = tool.SessionStartMessage(ctx);
            if (!string.IsNullOrWhiteSpace(contribution))
                instructions.Add(contribution!);
        }

        return new ChatSession(req.WorktreeWorkspace, OrchestratorPrompts.HeadlessPreamble, instructions);
    }

    /// <summary>Per-turn watchdogs: a stall timer that fires when no stream event arrives for
    /// the stall timeout (every event resets it via <see cref="Pet"/>) and an absolute turn
    /// deadline. Whichever fires first cancels the turn and records itself so cancellation can
    /// be classified afterwards.</summary>
    private sealed class TurnWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Timer _stall;
        private readonly Timer _deadline;
        private readonly TimeSpan _stallTimeout;
        private int _fired; // 0 = none, 1 = stall, 2 = deadline

        public TurnWatchdog(CancellationToken external, TimeSpan stallTimeout, TimeSpan turnTimeout)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _stallTimeout = stallTimeout;
            _stall = new Timer(_ => Fire(1), null, stallTimeout, Timeout.InfiniteTimeSpan);
            _deadline = new Timer(_ => Fire(2), null, turnTimeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _cts.Token;
        public bool StallFired => Volatile.Read(ref _fired) == 1;
        public bool DeadlineFired => Volatile.Read(ref _fired) == 2;

        public void Pet() => _stall.Change(_stallTimeout, Timeout.InfiniteTimeSpan);

        private void Fire(int kind)
        {
            if (Interlocked.CompareExchange(ref _fired, kind, 0) != 0) return;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* turn already over */ }
        }

        public void Dispose()
        {
            _stall.Dispose();
            _deadline.Dispose();
            _cts.Dispose();
        }
    }
}
