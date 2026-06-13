using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardServiceTests
{
    private static BoardCard Card(string id, string title, CardPriority priority = CardPriority.Normal, string columnId = "c") =>
        new(id, title, null, columnId, "", Array.Empty<SubtaskItem>(), priority);

    [Test]
    public void SubtaskProgress_CountsDone()
    {
        var subtasks = new[] { new SubtaskItem("a", true), new SubtaskItem("b", false), new SubtaskItem("c", true) };
        Assert.That(BoardService.SubtaskProgress(subtasks), Is.EqualTo((2, 3)));
    }

    [Test]
    public void SubtaskProgress_Empty_ReturnsZeroZero()
        => Assert.That(BoardService.SubtaskProgress(Array.Empty<SubtaskItem>()), Is.EqualTo((0, 0)));

    [Test]
    public void PresumptiveBranch_IsIdPrefixedSlug()
    {
        Assert.That(BoardService.PresumptiveBranch("0001", "Test Card"), Is.EqualTo("0001-test-card"));
        Assert.That(BoardService.PresumptiveBranch("0008", "Add OAuth Login!"), Is.EqualTo("0008-add-oauth-login"));
        Assert.That(BoardService.PresumptiveBranch("0008", "   "), Is.EqualTo("0008"));
    }

    [Test]
    public void FormatCardId_ZeroPadsToFour()
    {
        Assert.That(BoardService.FormatCardId(1), Is.EqualTo("0001"));
        Assert.That(BoardService.FormatCardId(42), Is.EqualTo("0042"));
        Assert.That(BoardService.FormatCardId(12345), Is.EqualTo("12345"));
    }

    [Test]
    public void CompareCards_SortsByPriority_ThenNumericId()
    {
        var cards = new List<BoardCard>
        {
            Card("0003", "c"),
            Card("0001", "a", CardPriority.Low),
            Card("0002", "b", CardPriority.High),
        };
        cards.Sort(BoardService.CompareCards);

        Assert.That(cards.Select(c => c.Id), Is.EqualTo(new[] { "0002", "0003", "0001" }));
    }

    [Test]
    public void Snapshot_FindCard_ByExactAndNumericId()
    {
        var snap = new BoardSnapshot(new[]
        {
            new BoardColumn("idea-id", 10, "idea", null, new[] { Card("0007", "Foo", columnId: "idea-id") }),
        });

        Assert.That(snap.FindCard("0007")?.Title, Is.EqualTo("Foo"));
        Assert.That(snap.FindCard("7")?.Title, Is.EqualTo("Foo"), "numeric form should match the padded id");
        Assert.That(snap.FindCard("nope"), Is.Null);
    }

    [Test]
    public void Snapshot_FindColumn_ByIdOrDisplayName_AndNextColumn()
    {
        var snap = new BoardSnapshot(new[]
        {
            new BoardColumn("idea-id", 10, "idea", null, Array.Empty<BoardCard>()),
            new BoardColumn("analyze-id", 20, "analyze", "go", Array.Empty<BoardCard>()),
        });

        var idea = snap.FindColumn("idea");
        Assert.That(idea, Is.Not.Null);
        Assert.That(snap.FindColumn("idea-id"), Is.EqualTo(idea));
        Assert.That(snap.NextColumn(idea!)?.DisplayName, Is.EqualTo("analyze"));
        Assert.That(snap.NextColumn(snap.FindColumn("analyze")!), Is.Null);
    }
}
