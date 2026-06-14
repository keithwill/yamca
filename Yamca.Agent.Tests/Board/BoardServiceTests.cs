using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardServiceTests
{
    private static BoardCard Card(int id, string title, CardPriority priority = CardPriority.Normal, string columnId = "c") =>
        new(id, title, null, columnId, "", Array.Empty<TaskItem>(), priority);

    [Test]
    public void TaskProgress_CountsDone()
    {
        var tasks = new[] { new TaskItem(1, "a", true), new TaskItem(2, "b", false), new TaskItem(3, "c", true) };
        Assert.That(BoardService.TaskProgress(tasks), Is.EqualTo((2, 3)));
    }

    [Test]
    public void TaskProgress_Empty_ReturnsZeroZero()
        => Assert.That(BoardService.TaskProgress(Array.Empty<TaskItem>()), Is.EqualTo((0, 0)));

    [Test]
    public void PresumptiveBranch_IsIdPrefixedSlug()
    {
        Assert.That(BoardService.PresumptiveBranch(1, "Test Card"), Is.EqualTo("1-test-card"));
        Assert.That(BoardService.PresumptiveBranch(8, "Add OAuth Login!"), Is.EqualTo("8-add-oauth-login"));
        Assert.That(BoardService.PresumptiveBranch(8, "   "), Is.EqualTo("8"));
    }

    [Test]
    public void CompareCards_SortsByPriority_ThenId()
    {
        var cards = new List<BoardCard>
        {
            Card(3, "c"),
            Card(1, "a", CardPriority.Low),
            Card(2, "b", CardPriority.High),
        };
        cards.Sort(BoardService.CompareCards);

        Assert.That(cards.Select(c => c.Id), Is.EqualTo(new[] { 2, 3, 1 }));
    }

    [Test]
    public void Snapshot_FindCard_ByIntAndTextualId()
    {
        var snap = new BoardSnapshot(new[]
        {
            new BoardColumn("idea-id", 10, "idea", null, new[] { Card(7, "Foo", columnId: "idea-id") }),
        });

        Assert.That(snap.FindCard(7)?.Title, Is.EqualTo("Foo"));
        Assert.That(snap.FindCard("7")?.Title, Is.EqualTo("Foo"));
        Assert.That(snap.FindCard("0007")?.Title, Is.EqualTo("Foo"), "leading zeros tolerated");
        Assert.That(snap.FindCard("nope"), Is.Null);
        Assert.That(snap.FindCard(99), Is.Null);
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
