using NUnit.Framework;
using VestPocket;
using Yamca.Agent.Board;
using Yamca.Agent.Storage;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardStoreTests
{
    private BoardStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        // A null file path opens an in-memory VestPocket store — no disk, no cleanup.
        _store = new BoardStore(new YamcaStore(filePath: null));
    }

    private async Task<(string idea, string analyze, string implement, string done)> ColumnsAsync()
    {
        var snap = await _store.ReadAsync(CancellationToken.None);
        return (
            snap.FindColumn("idea")!.Id,
            snap.FindColumn("analyze")!.Id,
            snap.FindColumn("implement")!.Id,
            snap.FindColumn("done")!.Id);
    }

    [Test]
    public async Task ReadAsync_SeedsDefaultColumns_InOrder()
    {
        var snap = await _store.ReadAsync(CancellationToken.None);

        Assert.That(snap.Columns.Select(c => c.DisplayName),
            Is.EqualTo(new[] { "idea", "analyze", "implement", "verify", "done" }));
        Assert.That(snap.Columns.Select(c => c.Order), Is.EqualTo(new[] { 10, 20, 30, 40, 50 }));
    }

    [Test]
    public async Task SeededColumns_HaveGeneratedIds_DistinctFromDisplayName()
    {
        var snap = await _store.ReadAsync(CancellationToken.None);
        var idea = snap.FindColumn("idea")!;

        Assert.That(idea.Id, Is.Not.EqualTo("idea"));
        Assert.That(idea.Id, Has.Length.GreaterThan(8));
        // Work columns carry instructions; resting columns don't.
        Assert.That(snap.FindColumn("analyze")!.Instructions, Is.Not.Null.And.Not.Empty);
        Assert.That(idea.Instructions, Is.Null);
    }

    [Test]
    public async Task EnsureSeeded_IsIdempotent()
    {
        await _store.EnsureSeededAsync(CancellationToken.None);
        await _store.EnsureSeededAsync(CancellationToken.None);

        var snap = await _store.ReadAsync(CancellationToken.None);
        Assert.That(snap.Columns, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task AddCard_AssignsSequentialId_StartingAtOne_AndStoresFields()
    {
        var (idea, _, _, _) = await ColumnsAsync();

        // The body is stored verbatim now (checklist-looking lines are just prose); tasks start empty
        // and are added through the task tools afterward.
        var id = await _store.AddCardAsync(idea, "Add OAuth", "Plan it\n- [ ] step one", "feat/oauth", CardPriority.High, CancellationToken.None);

        Assert.That(id, Is.EqualTo(1));
        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(1)!;
        Assert.That(card.Title, Is.EqualTo("Add OAuth"));
        Assert.That(card.Branch, Is.EqualTo("feat/oauth"));
        Assert.That(card.Priority, Is.EqualTo(CardPriority.High));
        Assert.That(card.ColumnId, Is.EqualTo(idea));
        Assert.That(card.Body, Is.EqualTo("Plan it\n- [ ] step one"));
        Assert.That(card.Tasks, Is.Empty);
    }

    [Test]
    public async Task NextCardId_IsLastAssignedPlusOne()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        await _store.AddCardAsync(idea, "a", "", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddCardAsync(idea, "b", "", null, CardPriority.Normal, CancellationToken.None);

        Assert.That(await _store.NextCardIdAsync(CancellationToken.None), Is.EqualTo(3));
    }

    [Test]
    public async Task AddCard_DoesNotReuseDeletedIds()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var first = await _store.AddCardAsync(idea, "a", "", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddCardAsync(idea, "b", "", null, CardPriority.Normal, CancellationToken.None);

        // Delete the most recent card; its id must not be handed out again.
        Assert.That(await _store.DeleteCardAsync(2, CancellationToken.None), Is.True);
        Assert.That(await _store.NextCardIdAsync(CancellationToken.None), Is.EqualTo(3));

        var third = await _store.AddCardAsync(idea, "c", "", null, CardPriority.Normal, CancellationToken.None);
        Assert.That(third, Is.EqualTo(3));
        Assert.That(first, Is.EqualTo(1));
    }

    [Test]
    public async Task MoveCard_RewritesColumnId()
    {
        var (idea, analyze, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "", null, CardPriority.Normal, CancellationToken.None);

        Assert.That(await _store.MoveCardAsync(id, analyze, CancellationToken.None), Is.True);

        var snap = await _store.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindColumn("idea")!.Cards, Is.Empty);
        Assert.That(snap.FindColumn("analyze")!.Cards.Single().Id, Is.EqualTo(id));
    }

    [Test]
    public async Task MoveCard_MissingCard_ReturnsFalse()
        => Assert.That(await _store.MoveCardAsync(9999, "nope", CancellationToken.None), Is.False);

    [Test]
    public async Task DeleteCard_RemovesIt()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "", null, CardPriority.Normal, CancellationToken.None);

        Assert.That(await _store.DeleteCardAsync(id, CancellationToken.None), Is.True);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindCard(id), Is.Null);
    }

    [Test]
    public async Task UpdateCardContent_AppliesParsedFields_AndLeavesTasksUntouched()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "old", "old body", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddTasksAsync(id, new[] { "keep me" }, CancellationToken.None);

        // Checklist-looking lines in the content stay in the body verbatim; the card's tasks are a
        // separate collection that board_update_card does not touch.
        var parsed = CardMarkdown.Parse("---\ntitle: New\npriority: high\n---\nfresh body\n- [x] did it");
        Assert.That(await _store.UpdateCardContentAsync(id, parsed, CancellationToken.None), Is.True);

        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Title, Is.EqualTo("New"));
        Assert.That(card.Priority, Is.EqualTo(CardPriority.High));
        Assert.That(card.Body, Is.EqualTo("fresh body\n- [x] did it"));
        Assert.That(card.Tasks.Select(t => t.Text), Is.EqualTo(new[] { "keep me" }));
    }

    [Test]
    public async Task AddTasks_AssignsSequentialIds_AndReturnsList()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "ask", null, CardPriority.Normal, CancellationToken.None);

        var afterFirst = await _store.AddTasksAsync(id, new[] { "a", "b" }, CancellationToken.None);
        Assert.That(afterFirst!.Select(t => (t.Id, t.Text, t.Done)),
            Is.EqualTo(new[] { (1, "a", false), (2, "b", false) }));

        // A second add continues the id sequence and skips blank texts.
        var afterSecond = await _store.AddTasksAsync(id, new[] { "  ", "c" }, CancellationToken.None);
        Assert.That(afterSecond!.Select(t => t.Id), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(afterSecond![^1].Text, Is.EqualTo("c"));
    }

    [Test]
    public async Task AddTasks_MissingCard_ReturnsNull()
        => Assert.That(await _store.AddTasksAsync(9999, new[] { "x" }, CancellationToken.None), Is.Null);

    [Test]
    public async Task SetTaskDone_TicksAndUnticks_ByTaskId()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddTasksAsync(id, new[] { "a", "b" }, CancellationToken.None);

        Assert.That(await _store.SetTaskDoneAsync(id, 2, true, CancellationToken.None), Is.True);
        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Tasks.Single(t => t.Id == 2).Done, Is.True);
        Assert.That(card.Tasks.Single(t => t.Id == 1).Done, Is.False);

        Assert.That(await _store.SetTaskDoneAsync(id, 2, false, CancellationToken.None), Is.True);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindCard(id)!.Tasks.Single(t => t.Id == 2).Done, Is.False);

        // Unknown task id is a clean failure.
        Assert.That(await _store.SetTaskDoneAsync(id, 99, true, CancellationToken.None), Is.False);
    }

    [Test]
    public async Task UpdateTaskText_RewritesText_RejectsBlank_AndMissingTask()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddTasksAsync(id, new[] { "old" }, CancellationToken.None);

        Assert.That(await _store.UpdateTaskTextAsync(id, 1, "new", CancellationToken.None), Is.True);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindCard(id)!.Tasks.Single().Text, Is.EqualTo("new"));

        Assert.That(await _store.UpdateTaskTextAsync(id, 1, "   ", CancellationToken.None), Is.False);
        Assert.That(await _store.UpdateTaskTextAsync(id, 99, "x", CancellationToken.None), Is.False);
    }

    [Test]
    public async Task RemoveTask_DeletesIt_AndDoesNotReuseTheId()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddTasksAsync(id, new[] { "a", "b" }, CancellationToken.None);

        Assert.That(await _store.RemoveTaskAsync(id, 2, CancellationToken.None), Is.True);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindCard(id)!.Tasks.Select(t => t.Id),
            Is.EqualTo(new[] { 1 }));

        // The counter does not rewind: the next added task takes 3, not the freed 2.
        var after = await _store.AddTasksAsync(id, new[] { "c" }, CancellationToken.None);
        Assert.That(after!.Select(t => t.Id), Is.EqualTo(new[] { 1, 3 }));

        Assert.That(await _store.RemoveTaskAsync(id, 99, CancellationToken.None), Is.False);
    }

    [Test]
    public async Task LegacyTasks_WithoutIds_AreRenumbered_OnReadAndOnMutation()
    {
        // Simulate a card persisted before tasks carried ids (the old "Subtasks" shape): every id 0.
        var yamca = new YamcaStore(filePath: null);
        var store = new BoardStore(yamca);
        var idea = (await store.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Id;

        var legacy = new CardRecord(1, "legacy", null, CardPriority.Normal, idea, "ask",
            new[] { new TaskState(0, "first", false), new TaskState(0, "second", true) });
        var vp = await yamca.GetAsync(CancellationToken.None);
        await vp.Save(new Kvp("/board/card/1", legacy));

        // Read projection renumbers positionally 1..n.
        var card = (await store.ReadAsync(CancellationToken.None)).FindCard(1)!;
        Assert.That(card.Tasks.Select(t => (t.Id, t.Text)), Is.EqualTo(new[] { (1, "first"), (2, "second") }));

        // A by-id mutation persists those ids, and a subsequent add continues past them (no reuse).
        Assert.That(await store.SetTaskDoneAsync(1, 1, true, CancellationToken.None), Is.True);
        var added = await store.AddTasksAsync(1, new[] { "third" }, CancellationToken.None);
        Assert.That(added!.Select(t => t.Id), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task SetPriority_And_SetBranch()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "", null, CardPriority.Normal, CancellationToken.None);

        await _store.SetPriorityAsync(id, CardPriority.Low, CancellationToken.None);
        await _store.SetBranchAsync(id, "feat/x", CancellationToken.None);

        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Priority, Is.EqualTo(CardPriority.Low));
        Assert.That(card.Branch, Is.EqualTo("feat/x"));
    }

    [Test]
    public async Task SetArtifact_AddsReplacesAndRemoves_ByKind()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "the original ask", null, CardPriority.Normal, CancellationToken.None);

        // Add two artifacts of distinct kinds.
        Assert.That(await _store.SetArtifactAsync(id, "plan", "step 1\nstep 2", CancellationToken.None), Is.True);
        Assert.That(await _store.SetArtifactAsync(id, "build-log", "all green", CancellationToken.None), Is.True);

        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Body, Is.EqualTo("the original ask"), "artifacts must not touch the body");
        Assert.That(card.Artifacts.Select(a => a.Kind), Is.EquivalentTo(new[] { "plan", "build-log" }));
        Assert.That(card.FindArtifact("plan")!.Content, Is.EqualTo("step 1\nstep 2"));

        // Re-setting the same kind (case-insensitive) replaces its content in place.
        Assert.That(await _store.SetArtifactAsync(id, "PLAN", "rewritten", CancellationToken.None), Is.True);
        card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Artifacts, Has.Count.EqualTo(2));
        Assert.That(card.FindArtifact("plan")!.Content, Is.EqualTo("rewritten"));

        // Blank content removes the artifact.
        Assert.That(await _store.SetArtifactAsync(id, "plan", "   ", CancellationToken.None), Is.True);
        card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Artifacts.Select(a => a.Kind), Is.EqualTo(new[] { "build-log" }));
    }

    [Test]
    public async Task SetArtifact_MissingCard_ReturnsFalse()
        => Assert.That(await _store.SetArtifactAsync(9999, "plan", "x", CancellationToken.None), Is.False);

    [Test]
    public async Task Card_StoredWithNullArtifacts_ProjectsToEmpty_NotNull()
    {
        // A card persisted before the Artifacts field existed deserializes with a null list (STJ does
        // not honor the field initializer for an omitted property). Reading the board must still yield
        // an empty, non-null list rather than NPE in the projection.
        var yamca = new YamcaStore(filePath: null);
        var store = new BoardStore(yamca);
        var idea = (await store.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Id;

        var legacy = new CardRecord(1, "legacy", null, CardPriority.Normal, idea, "body",
            Array.Empty<TaskState>()) { Artifacts = null };
        var vp = await yamca.GetAsync(CancellationToken.None);
        await vp.Save(new Kvp("/board/card/1", legacy));

        var card = (await store.ReadAsync(CancellationToken.None)).FindCard(1)!;
        Assert.That(card.Artifacts, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task Artifacts_SurviveBodyAndColumnUpdates()
    {
        var (idea, analyze, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "card", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _store.SetArtifactAsync(id, "plan", "the plan", CancellationToken.None);

        // A body edit and a move must leave the artifact intact (the card aggregate carries it along).
        // The body is stored verbatim (checklist-looking lines are just prose now).
        await _store.UpdateCardBodyAsync(id, "card", "revised ask\n- [ ] todo", CancellationToken.None);
        await _store.MoveCardAsync(id, analyze, CancellationToken.None);

        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Body, Is.EqualTo("revised ask\n- [ ] todo"));
        Assert.That(card.FindArtifact("plan")!.Content, Is.EqualTo("the plan"));
    }

    [Test]
    public async Task SetColumnInstructions_TogglesWorkColumn()
    {
        var (idea, _, _, _) = await ColumnsAsync();

        Assert.That(await _store.SetColumnInstructionsAsync(idea, "Now do planning.", CancellationToken.None), Is.True);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Instructions, Is.EqualTo("Now do planning."));

        await _store.SetColumnInstructionsAsync(idea, "   ", CancellationToken.None);
        Assert.That((await _store.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Instructions, Is.Null);
    }

    [Test]
    public async Task Reinit_MovesOrphanCardsToIdea_AndPreservesDefaults()
    {
        var (idea, analyze, _, _) = await ColumnsAsync();
        var kept = await _store.AddCardAsync(analyze, "kept", "", null, CardPriority.Normal, CancellationToken.None);
        var orphanCardId = await _store.AddCardAsync(idea, "orphan", "", null, CardPriority.Normal, CancellationToken.None);
        // Manufacture an orphan by moving a card to a column id that doesn't exist.
        await _store.MoveCardAsync(orphanCardId, "ghost-column", CancellationToken.None);

        var result = await _store.ReinitAsync(wipe: false, CancellationToken.None);

        Assert.That(result.CardsPreserved, Is.EqualTo(1));
        Assert.That(result.CardsMoved, Is.EqualTo(1));
        var snap = await _store.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindColumn("analyze")!.Cards.Single().Id, Is.EqualTo(kept));
        Assert.That(snap.FindColumn("idea")!.Cards.Single().Id, Is.EqualTo(orphanCardId));
    }

    [Test]
    public async Task Reinit_Wipe_DeletesAllCards_AndResetsIdCounter()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        await _store.AddCardAsync(idea, "a", "", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddCardAsync(idea, "b", "", null, CardPriority.Normal, CancellationToken.None);

        var result = await _store.ReinitAsync(wipe: true, CancellationToken.None);

        Assert.That(result.CardsWiped, Is.EqualTo(2));
        Assert.That((await _store.ReadAsync(CancellationToken.None)).AllCards, Is.Empty);

        // A wipe is a full start-over, so numbering restarts at 1.
        Assert.That(await _store.NextCardIdAsync(CancellationToken.None), Is.EqualTo(1));
        Assert.That(await _store.AddCardAsync(idea, "fresh", "", null, CardPriority.Normal, CancellationToken.None),
            Is.EqualTo(1));
    }

    [Test]
    public async Task Reinit_NonWipe_KeepsIdCounter_SoSurvivingIdsAreNotReused()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        await _store.AddCardAsync(idea, "a", "", null, CardPriority.Normal, CancellationToken.None);
        await _store.AddCardAsync(idea, "b", "", null, CardPriority.Normal, CancellationToken.None);

        // Non-wipe reinit preserves the cards, so the counter must stay advanced past them.
        await _store.ReinitAsync(wipe: false, CancellationToken.None);

        Assert.That(await _store.NextCardIdAsync(CancellationToken.None), Is.EqualTo(3));
        Assert.That(await _store.AddCardAsync(idea, "c", "", null, CardPriority.Normal, CancellationToken.None),
            Is.EqualTo(3));
    }

    [Test]
    public async Task ConcurrentAdds_GetDistinctIds()
    {
        var (idea, _, _, _) = await ColumnsAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            _store.AddCardAsync(idea, $"card {i}", "", null, CardPriority.Normal, CancellationToken.None)));

        var cards = (await _store.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Cards;
        Assert.That(cards, Has.Count.EqualTo(8));
        Assert.That(cards.Select(c => c.Id).Distinct().Count(), Is.EqualTo(8), "ids must not collide under concurrency");
    }
}
