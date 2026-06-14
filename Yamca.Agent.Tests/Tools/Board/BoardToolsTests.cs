using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;
using Yamca.Agent.Storage;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.Board;

namespace Yamca.Agent.Tests.Tools.Board;

[TestFixture]
public class BoardToolsTests
{
    private TempWorkspace _ws = null!;
    private BoardStore _boardStore = null!;
    private string _idea = null!;
    private string _analyze = null!;

    [SetUp]
    public async Task SetUp()
    {
        _ws = new TempWorkspace();
        // The board is backed by an in-memory store; the workspace only serves the ToolContext.
        _boardStore = new BoardStore(new YamcaStore(filePath: null));
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        _idea = snap.FindColumn("idea")!.Id;
        _analyze = snap.FindColumn("analyze")!.Id;
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: true);

    [Test]
    public async Task BoardList_FormatsColumnsCardsAndProgress()
    {
        await _boardStore.AddCardAsync(_idea, "Add OAuth", "- [x] a\n- [ ] b", "feat/oauth", CardPriority.Normal, CancellationToken.None);

        var result = await new BoardListTool(_boardStore).ExecuteAsync(
            Json.Parse("{}"), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("## idea"));
        Assert.That(result.Content, Does.Contain("#1 Add OAuth"));
        Assert.That(result.Content, Does.Contain("[1/2]"));
        Assert.That(result.Content, Does.Contain("branch: feat/oauth"));
        Assert.That(result.Content, Does.Contain("## analyze"));
    }

    [Test]
    public async Task BoardGetCard_ReturnsRenderedMarkdown()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Body\ntext", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("# Body"));
        Assert.That(result.Content, Does.Contain("id: 1"));
        Assert.That(result.Content, Does.Contain("in column 'idea'"));
    }

    [Test]
    public async Task BoardGetCard_UnknownCard_Error()
    {
        var result = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "999" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetStepInstructions_ReturnsContent_AndNoteWhenMissing()
    {
        var present = await new BoardGetStepInstructionsTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "implement" }"""), Ctx(), CancellationToken.None);
        Assert.That(present.IsError, Is.False, present.Content);
        Assert.That(present.Content, Does.Contain("Implement"));

        var missing = await new BoardGetStepInstructionsTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "done" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.False);
        Assert.That(missing.Content, Does.Contain("no step instructions"));
    }

    [Test]
    public async Task BoardMoveCard_RewritesColumn()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "analyze" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindCard("0001")!.ColumnId, Is.EqualTo(_analyze));
    }

    [Test]
    public async Task BoardMoveCard_UnknownColumn_Error()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);
        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardUpdateCard_AppliesContent()
    {
        await _boardStore.AddCardAsync(_idea, "old", "old", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardUpdateCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "content": "new content\n- [x] done" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var card = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!;
        Assert.That(card.Body, Is.EqualTo("new content"));
        Assert.That(card.Subtasks.Single(), Is.EqualTo(new SubtaskItem("done", true)));
    }

    [Test]
    public void PermissionDefaults_ReadsAllow_MutationsAsk()
    {
        Assert.That(new BoardListTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetStepInstructionsTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardMoveCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }
}
