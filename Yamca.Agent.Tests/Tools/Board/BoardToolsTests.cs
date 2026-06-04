using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.Board;

namespace Yamca.Agent.Tests.Tools.Board;

[TestFixture]
public class BoardToolsTests
{
    private TempWorkspace _ws = null!;
    private BoardService _board = null!;
    private BoardStore _boardStore = null!;
    private string _boardPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _ws = new TempWorkspace();
        _board = new BoardService();

        // The board is a plain directory under the repository root; bootstrap it + default columns.
        _boardStore = new BoardStore(_ws.Workspace);
        _boardPath = await _boardStore.EnsureAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_ws.RootPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
        }
        catch { /* best-effort */ }
        _ws.Dispose();
    }

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: true);

    // Card paths are relative to the board directory (at <root>/.yamca/board).
    private string Board(string relative) => $".yamca/board/{relative}";

    [Test]
    public async Task BoardList_FormatsColumnsCardsAndProgress()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\ntitle: Add OAuth\nbranch: feat/oauth\n---\n- [x] a\n- [ ] b");

        var result = await new BoardListTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("{}"), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("## idea"));
        Assert.That(result.Content, Does.Contain("#7 Add OAuth"));
        Assert.That(result.Content, Does.Contain("[1/2]"));
        Assert.That(result.Content, Does.Contain("branch: feat/oauth"));
        Assert.That(result.Content, Does.Contain("## analyze"));
    }

    [Test]
    public async Task BoardGetCard_ReturnsRawContent()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\n---\n# Body\ntext");

        var result = await new BoardGetCardTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "7" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("# Body"));
        Assert.That(result.Content, Does.Contain("id: 7"));
    }

    [Test]
    public async Task BoardGetCard_UnknownCard_Error()
    {
        var result = await new BoardGetCardTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "999" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetStepInstructions_ReturnsContent_AndNoteWhenMissing()
    {
        // Overwrite a work column's seeded instructions, and use a resting column (done, seeded empty)
        // for the missing case.
        _ws.WriteFile(Board("30-implement/instructions.md"), "Write the code.");

        var present = await new BoardGetStepInstructionsTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "implement" }"""), Ctx(), CancellationToken.None);
        Assert.That(present.IsError, Is.False, present.Content);
        Assert.That(present.Content, Does.Contain("Write the code."));

        var missing = await new BoardGetStepInstructionsTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "done" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.False);
        Assert.That(missing.Content, Does.Contain("no instructions"));
    }

    [Test]
    public async Task BoardMoveCard_RelocatesCardFile()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\ntitle: OAuth\n---\n# Card");

        var result = await new BoardMoveCardTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "analyze" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_boardPath, "20-analyze", "0007-oauth.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(_boardPath, "10-idea", "0007-oauth.md")), Is.False);
    }

    [Test]
    public async Task BoardMoveCard_UnknownColumn_Error()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "# Card");
        var result = await new BoardMoveCardTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardUpdateCard_WritesContent()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "old");

        var result = await new BoardUpdateCardTool(_board, _boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "7", "content": "new content\n- [x] done" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var text = await File.ReadAllTextAsync(Path.Combine(_boardPath, "10-idea", "0007-oauth.md"));
        Assert.That(text, Is.EqualTo("new content\n- [x] done"));
    }

    [Test]
    public void PermissionDefaults_ReadsAllow_MutationsAsk()
    {
        Assert.That(new BoardListTool(_board, _boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetCardTool(_board, _boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetStepInstructionsTool(_board, _boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardMoveCardTool(_board, _boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateCardTool(_board, _boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }
}
