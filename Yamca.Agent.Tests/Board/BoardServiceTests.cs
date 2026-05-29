using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Tests.Support;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardServiceTests
{
    private TempWorkspace _ws = null!;
    private BoardService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _svc = new BoardService();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private string Board(string relative) => $".yamca/board/{relative}";

    [Test]
    public void Read_MissingBoardDir_ReturnsEmpty()
    {
        var snapshot = _svc.Read(_ws.RootPath);
        Assert.That(snapshot.Columns, Is.Empty);
    }

    [Test]
    public void Read_OrdersColumnsByNumericPrefix_AndStripsPrefixForDisplay()
    {
        _ws.WriteFile(Board("30-implement/.keep"), "");
        _ws.WriteFile(Board("10-idea/.keep"), "");
        _ws.WriteFile(Board("20-analyze/.keep"), "");

        var snapshot = _svc.Read(_ws.RootPath);

        Assert.That(snapshot.Columns.Select(c => c.DisplayName),
            Is.EqualTo(new[] { "idea", "analyze", "implement" }));
        Assert.That(snapshot.Columns[0].Order, Is.EqualTo(10));
    }

    [Test]
    public void Read_IgnoresNonPrefixedDirectories()
    {
        _ws.WriteFile(Board("10-idea/.keep"), "");
        _ws.WriteFile(Board("worktrees/whatever.txt"), "");
        _ws.WriteFile(Board("notacolumn/.keep"), "");

        var snapshot = _svc.Read(_ws.RootPath);

        Assert.That(snapshot.Columns.Select(c => c.DirectoryName), Is.EqualTo(new[] { "10-idea" }));
    }

    [Test]
    public void Read_ExcludesInstructionsFile_FromCards()
    {
        _ws.WriteFile(Board("10-idea/instructions.md"), "Do planning here.");
        _ws.WriteFile(Board("10-idea/0001-thing.md"), "# Thing");

        var snapshot = _svc.Read(_ws.RootPath);

        Assert.That(snapshot.Columns[0].Cards.Select(c => c.FileName), Is.EqualTo(new[] { "0001-thing.md" }));
    }

    [Test]
    public void ParseCard_PrefersFrontmatter_ForIdTitleBranch()
    {
        var card = _svc.ParseCard("10-idea", "/x/0007-foo.md",
            "---\nid: 7\ntitle: Add OAuth login\nbranch: feat/oauth\n---\n\n# Ignored heading\nbody text");

        Assert.That(card.Id, Is.EqualTo("7"));
        Assert.That(card.Title, Is.EqualTo("Add OAuth login"));
        Assert.That(card.Branch, Is.EqualTo("feat/oauth"));
        Assert.That(card.Body, Does.Contain("body text"));
        Assert.That(card.ColumnDirectory, Is.EqualTo("10-idea"));
    }

    [Test]
    public void ParseCard_FallsBackToFilenameDigits_ThenHeading()
    {
        var card = _svc.ParseCard("10-idea", "/x/0042-whatever.md", "# Real Title\n\ndetails");

        Assert.That(card.Id, Is.EqualTo("0042"));
        Assert.That(card.Title, Is.EqualTo("Real Title"));
        Assert.That(card.Branch, Is.Null);
    }

    [Test]
    public void ParseCard_NoFrontmatterNoHeading_UsesStem()
    {
        var card = _svc.ParseCard("10-idea", "/x/loose-notes.md", "just some text");

        Assert.That(card.Id, Is.EqualTo("loose-notes"));
        Assert.That(card.Title, Is.EqualTo("loose-notes"));
    }

    [Test]
    public void ParseCard_MalformedFrontmatter_TreatedAsBody()
    {
        var card = _svc.ParseCard("10-idea", "/x/0001-a.md", "---\nthis never closes\nmore");
        Assert.That(card.Body, Does.Contain("never closes"));
        Assert.That(card.Id, Is.EqualTo("0001"));
    }

    [Test]
    public void SubtaskProgress_CountsCheckedAndUnchecked_CaseInsensitive()
    {
        var body = "- [ ] one\n- [x] two\n- [X] three\nnot a task\n  - [ ] indented four";
        Assert.That(BoardService.SubtaskProgress(body), Is.EqualTo((2, 4)));
    }

    [Test]
    public void SubtaskProgress_NoTasks_ReturnsZeroZero()
    {
        Assert.That(BoardService.SubtaskProgress("plain body\nwith lines"), Is.EqualTo((0, 0)));
    }

    [Test]
    public void NextCardId_TakesMaxAcrossColumns_PlusOne_ZeroPadded()
    {
        _ws.WriteFile(Board("10-idea/0003-a.md"), "# A");
        _ws.WriteFile(Board("30-implement/0009-b.md"), "---\nid: 9\n---\n# B");

        Assert.That(_svc.NextCardId(_ws.RootPath), Is.EqualTo("0010"));
    }

    [Test]
    public void NextCardId_EmptyBoard_IsZeroOne()
    {
        Assert.That(_svc.NextCardId(_ws.RootPath), Is.EqualTo("0001"));
    }

    [Test]
    public void CardFileName_Slugifies()
    {
        Assert.That(BoardService.CardFileName("0008", "Add OAuth Login!"), Is.EqualTo("0008-add-oauth-login.md"));
        Assert.That(BoardService.CardFileName("0008", "   "), Is.EqualTo("0008.md"));
    }

    [Test]
    public void PresumptiveBranch_IsIdPrefixedSlug()
    {
        Assert.That(BoardService.PresumptiveBranch("0001", "Test Card"), Is.EqualTo("0001-test-card"));
        Assert.That(BoardService.PresumptiveBranch("0008", "Add OAuth Login!"), Is.EqualTo("0008-add-oauth-login"));
        Assert.That(BoardService.PresumptiveBranch("0008", "   "), Is.EqualTo("0008"));
    }

    [Test]
    public void ReadInstructions_ReturnsContentOrNull()
    {
        _ws.WriteFile(Board("10-idea/instructions.md"), "plan it");
        Assert.That(_svc.ReadInstructions(_ws.RootPath, "10-idea"), Is.EqualTo("plan it"));
        Assert.That(_svc.ReadInstructions(_ws.RootPath, "20-analyze"), Is.Null);
    }

    [Test]
    public void WithBranch_AddsOrReplacesFrontmatterBranch()
    {
        var added = BoardService.WithBranch("# Title\nbody", "feat/x");
        Assert.That(added, Does.StartWith("---\nbranch: feat/x\n---\n"));
        Assert.That(added, Does.Contain("# Title\nbody"));

        var withFm = "---\nid: 7\ntitle: Foo\n---\n\nbody here";
        var replaced = BoardService.WithBranch(withFm, "feat/y");
        Assert.That(replaced, Does.Contain("branch: feat/y"));
        Assert.That(replaced, Does.Contain("id: 7"));
        Assert.That(replaced, Does.Contain("body here"));

        // Replacing an existing branch line should not duplicate it.
        var twice = BoardService.WithBranch(replaced, "feat/z");
        Assert.That(twice.Split("branch:").Length, Is.EqualTo(2));
        Assert.That(twice, Does.Contain("branch: feat/z"));
    }

    [Test]
    public void WithBranch_RoundTripsThroughParse()
    {
        var updated = BoardService.WithBranch("---\nid: 5\ntitle: T\n---\nbody", "feat/round");
        var card = _svc.ParseCard("10-idea", "/x/0005-t.md", updated);
        Assert.That(card.Branch, Is.EqualTo("feat/round"));
        Assert.That(card.Id, Is.EqualTo("5"));
    }

    [Test]
    public void Snapshot_FindCard_And_FindColumn()
    {
        _ws.WriteFile(Board("10-idea/0007-foo.md"), "---\nid: 7\ntitle: Foo\n---\nbody");
        _ws.WriteFile(Board("20-analyze/.keep"), "");

        var snapshot = _svc.Read(_ws.RootPath);

        Assert.That(snapshot.FindCard("7")?.Title, Is.EqualTo("Foo"));
        Assert.That(snapshot.FindCard("0007-foo")?.Title, Is.EqualTo("Foo"));
        Assert.That(snapshot.FindCard("nope"), Is.Null);

        var idea = snapshot.FindColumn("idea");
        Assert.That(idea, Is.Not.Null);
        Assert.That(snapshot.FindColumn("10-idea"), Is.EqualTo(idea));
        Assert.That(snapshot.NextColumn(idea!)?.DisplayName, Is.EqualTo("analyze"));
    }
}
