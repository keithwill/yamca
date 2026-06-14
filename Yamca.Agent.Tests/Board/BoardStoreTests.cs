using NUnit.Framework;
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

        var id = await _store.AddCardAsync(idea, "Add OAuth", "Plan it\n- [ ] step one\n- [x] step two", "feat/oauth", CardPriority.High, CancellationToken.None);

        Assert.That(id, Is.EqualTo(1));
        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(1)!;
        Assert.That(card.Title, Is.EqualTo("Add OAuth"));
        Assert.That(card.Branch, Is.EqualTo("feat/oauth"));
        Assert.That(card.Priority, Is.EqualTo(CardPriority.High));
        Assert.That(card.ColumnId, Is.EqualTo(idea));
        Assert.That(card.Body, Is.EqualTo("Plan it"));
        Assert.That(card.Subtasks, Has.Count.EqualTo(2));
        Assert.That(card.Subtasks[0], Is.EqualTo(new SubtaskItem("step one", false)));
        Assert.That(card.Subtasks[1], Is.EqualTo(new SubtaskItem("step two", true)));
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
    public async Task UpdateCardContent_AppliesParsedFields()
    {
        var (idea, _, _, _) = await ColumnsAsync();
        var id = await _store.AddCardAsync(idea, "old", "old body", null, CardPriority.Normal, CancellationToken.None);

        var parsed = CardMarkdown.Parse("---\ntitle: New\npriority: high\n---\nfresh body\n- [x] did it");
        Assert.That(await _store.UpdateCardContentAsync(id, parsed, CancellationToken.None), Is.True);

        var card = (await _store.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(card.Title, Is.EqualTo("New"));
        Assert.That(card.Priority, Is.EqualTo(CardPriority.High));
        Assert.That(card.Body, Is.EqualTo("fresh body"));
        Assert.That(card.Subtasks.Single(), Is.EqualTo(new SubtaskItem("did it", true)));
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
