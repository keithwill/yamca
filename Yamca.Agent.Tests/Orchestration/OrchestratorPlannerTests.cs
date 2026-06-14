using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Orchestration;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Orchestration;

[TestFixture]
public class OrchestratorPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static BoardCard Card(int id, string column, CardPriority priority = CardPriority.Normal) =>
        new(id, $"Card {id}", null, column, "", Array.Empty<TaskItem>(), priority);

    private static BoardColumn Column(string dir, int order, params BoardCard[] cards) =>
        new(dir, order, dir.Length > 3 ? dir[3..] : dir, null, cards);

    private static OrchestratorSettings Settings(params string[] enabledColumns) =>
        OrchestratorSettings.Default with { EnabledColumns = enabledColumns };

    private static CardOrchestrationState State(int cardId, string column, CardRunStatus status,
        DateTimeOffset? due = null) =>
        new(cardId.ToString(), column, status, Attempts: 1, NextAttemptUtc: due, FailureReason: null,
            ActiveRunId: status is CardRunStatus.Queued or CardRunStatus.Running ? "run" : null);

    private static Dictionary<string, CardOrchestrationState> States(params CardOrchestrationState[] states) =>
        states.ToDictionary(s => s.CardId, StringComparer.OrdinalIgnoreCase);

    // --- SelectCandidates ---------------------------------------------------------------

    [Test]
    public void SelectCandidates_OnlyEnabledColumns()
    {
        var board = new BoardSnapshot(new[]
        {
            Column("20-analyze", 20, Card(1, "20-analyze")),
            Column("30-implement", 30, Card(2, "30-implement")),
        });

        var got = OrchestratorPlanner.SelectCandidates(board, Settings("20-analyze"), States(), Now);

        Assert.That(got.Select(c => c.Card.Id), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SelectCandidates_SkipsClaimedParkedAndUndueRetries()
    {
        var board = new BoardSnapshot(new[]
        {
            Column("20-analyze", 20,
                Card(1, "20-analyze"),   // queued → skip
                Card(2, "20-analyze"),   // running → skip
                Card(3, "20-analyze"),   // parked → skip
                Card(4, "20-analyze"),   // retrying, not due → skip
                Card(5, "20-analyze"),   // retrying, due → eligible
                Card(6, "20-analyze")),  // untracked → eligible
        });
        var states = States(
            State(1, "20-analyze", CardRunStatus.Queued),
            State(2, "20-analyze", CardRunStatus.Running),
            State(3, "20-analyze", CardRunStatus.Parked),
            State(4, "20-analyze", CardRunStatus.Retrying, Now.AddMinutes(5)),
            State(5, "20-analyze", CardRunStatus.Retrying, Now.AddMinutes(-1)));

        var got = OrchestratorPlanner.SelectCandidates(board, Settings("20-analyze"), states, Now);

        Assert.That(got.Select(c => c.Card.Id), Is.EqualTo(new[] { 5, 6 }));
    }

    [Test]
    public void SelectCandidates_SortsPriorityThenId_AcrossColumns()
    {
        var board = new BoardSnapshot(new[]
        {
            Column("20-analyze", 20,
                Card(4, "20-analyze"),
                Card(2, "20-analyze", CardPriority.Low)),
            Column("30-implement", 30,
                Card(3, "30-implement", CardPriority.High),
                Card(1, "30-implement")),
        });

        var got = OrchestratorPlanner.SelectCandidates(
            board, Settings("20-analyze", "30-implement"), States(), Now);

        // High first, then normal oldest-id first, then low.
        Assert.That(got.Select(c => c.Card.Id), Is.EqualTo(new[] { 3, 1, 4, 2 }));
    }

    // --- TakeDispatchable ---------------------------------------------------------------

    [Test]
    public void TakeDispatchable_HonorsGlobalSlots()
    {
        var candidates = new (BoardCard, BoardColumn)[]
        {
            (Card(1, "20-analyze"), Column("20-analyze", 20)),
            (Card(2, "20-analyze"), Column("20-analyze", 20)),
            (Card(3, "20-analyze"), Column("20-analyze", 20)),
        };
        var settings = Settings("20-analyze") with { MaxConcurrentRuns = 2 };

        var got = OrchestratorPlanner.TakeDispatchable(
            candidates, new Dictionary<string, int>(), runningTotal: 1, settings);

        Assert.That(got.Select(c => c.Card.Id), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void TakeDispatchable_NoSlots_ReturnsEmpty()
    {
        var candidates = new (BoardCard, BoardColumn)[]
        {
            (Card(1, "20-analyze"), Column("20-analyze", 20)),
        };
        var settings = Settings("20-analyze") with { MaxConcurrentRuns = 2 };

        var got = OrchestratorPlanner.TakeDispatchable(
            candidates, new Dictionary<string, int>(), runningTotal: 2, settings);

        Assert.That(got, Is.Empty);
    }

    [Test]
    public void TakeDispatchable_PerColumnCap_SkipsToOtherColumns()
    {
        var analyze = Column("20-analyze", 20);
        var implement = Column("30-implement", 30);
        var candidates = new (BoardCard, BoardColumn)[]
        {
            (Card(1, "20-analyze"), analyze),
            (Card(2, "20-analyze"), analyze),   // over the per-column cap → skipped
            (Card(3, "30-implement"), implement),
        };
        var settings = Settings("20-analyze", "30-implement") with
        {
            MaxConcurrentRuns = 8,
            MaxConcurrentRunsPerColumn = 1,
        };

        var got = OrchestratorPlanner.TakeDispatchable(
            candidates, new Dictionary<string, int>(), runningTotal: 0, settings);

        Assert.That(got.Select(c => c.Card.Id), Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void TakeDispatchable_PerColumnCap_CountsAlreadyRunning()
    {
        var analyze = Column("20-analyze", 20);
        var candidates = new (BoardCard, BoardColumn)[]
        {
            (Card(2, "20-analyze"), analyze),
        };
        var settings = Settings("20-analyze") with { MaxConcurrentRuns = 8, MaxConcurrentRunsPerColumn = 1 };
        var runningPerColumn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["20-analyze"] = 1,
        };

        var got = OrchestratorPlanner.TakeDispatchable(candidates, runningPerColumn, runningTotal: 1, settings);

        Assert.That(got, Is.Empty);
    }

    // --- Reconcile ------------------------------------------------------------------------

    [Test]
    public void Reconcile_CardStillInColumn_None()
    {
        var board = new BoardSnapshot(new[] { Column("20-analyze", 20, Card(1, "20-analyze")) });
        var state = State(1, "20-analyze", CardRunStatus.Running);

        Assert.That(OrchestratorPlanner.Reconcile(state, board, Settings("20-analyze")),
            Is.EqualTo(OrchestratorPlanner.ReconcileAction.None));
    }

    [Test]
    public void Reconcile_CardDeleted_CancelDeleted()
    {
        var board = new BoardSnapshot(new[] { Column("20-analyze", 20) });
        var state = State(1, "20-analyze", CardRunStatus.Running);

        Assert.That(OrchestratorPlanner.Reconcile(state, board, Settings("20-analyze")),
            Is.EqualTo(OrchestratorPlanner.ReconcileAction.CancelDeleted));
    }

    [Test]
    public void Reconcile_CardLeftColumn_CompleteAsMoved()
    {
        var board = new BoardSnapshot(new[]
        {
            Column("20-analyze", 20),
            Column("30-implement", 30, Card(1, "30-implement")),
        });
        var state = State(1, "20-analyze", CardRunStatus.Running);

        Assert.That(OrchestratorPlanner.Reconcile(state, board, Settings("20-analyze")),
            Is.EqualTo(OrchestratorPlanner.ReconcileAction.CompleteAsMoved));
    }

    [Test]
    public void Reconcile_ColumnDisabledMidRun_CancelColumnDisabled()
    {
        var board = new BoardSnapshot(new[] { Column("20-analyze", 20, Card(1, "20-analyze")) });
        var state = State(1, "20-analyze", CardRunStatus.Running);

        Assert.That(OrchestratorPlanner.Reconcile(state, board, Settings("30-implement")),
            Is.EqualTo(OrchestratorPlanner.ReconcileAction.CancelColumnDisabled));
    }
}
