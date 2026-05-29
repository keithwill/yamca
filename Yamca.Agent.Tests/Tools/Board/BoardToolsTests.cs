using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Git;
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
    private GitService _git = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _board = new BoardService();
        _git = new GitService();
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
    private string Board(string relative) => $".yamca/board/{relative}";

    [Test]
    public async Task BoardList_FormatsColumnsCardsAndProgress()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\ntitle: Add OAuth\nbranch: feat/oauth\n---\n- [x] a\n- [ ] b");
        _ws.WriteFile(Board("20-analyze/.keep"), "");

        var result = await new BoardListTool(_board).ExecuteAsync(
            Json.Parse("{}"), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("## idea"));
        Assert.That(result.Content, Does.Contain("#7 Add OAuth"));
        Assert.That(result.Content, Does.Contain("[1/2]"));
        Assert.That(result.Content, Does.Contain("branch: feat/oauth"));
        Assert.That(result.Content, Does.Contain("## analyze"));
    }

    [Test]
    public async Task BoardList_EmptyBoard_OkNote()
    {
        var result = await new BoardListTool(_board).ExecuteAsync(Json.Parse("{}"), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("empty"));
    }

    [Test]
    public async Task BoardGetCard_ReturnsRawContent()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\n---\n# Body\ntext");

        var result = await new BoardGetCardTool(_board).ExecuteAsync(
            Json.Parse("""{ "card": "7" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("# Body"));
        Assert.That(result.Content, Does.Contain("id: 7"));
    }

    [Test]
    public async Task BoardGetCard_UnknownCard_Error()
    {
        var result = await new BoardGetCardTool(_board).ExecuteAsync(
            Json.Parse("""{ "card": "999" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetStepInstructions_ReturnsContent_AndNoteWhenMissing()
    {
        _ws.WriteFile(Board("30-implement/instructions.md"), "Write the code.");
        _ws.WriteFile(Board("40-verify/.keep"), "");

        var present = await new BoardGetStepInstructionsTool(_board).ExecuteAsync(
            Json.Parse("""{ "column": "implement" }"""), Ctx(), CancellationToken.None);
        Assert.That(present.IsError, Is.False, present.Content);
        Assert.That(present.Content, Does.Contain("Write the code."));

        var missing = await new BoardGetStepInstructionsTool(_board).ExecuteAsync(
            Json.Parse("""{ "column": "verify" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.False);
        Assert.That(missing.Content, Does.Contain("no instructions"));
    }

    [Test]
    public async Task BoardMoveCard_WithGit_StagesRename()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "# Card");
        _ws.WriteFile(Board("20-analyze/.keep"), "");
        await InitRepoAndCommitAll();

        var result = await new BoardMoveCardTool(_board, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "analyze" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_ws.RootPath, ".yamca/board/20-analyze/0007-oauth.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(_ws.RootPath, ".yamca/board/10-idea/0007-oauth.md")), Is.False);

        var status = await RunGitCapture("status", "--porcelain");
        Assert.That(status, Does.Contain("R"));      // staged rename
        Assert.That(result.Content, Does.Contain("staged"));
    }

    [Test]
    public async Task BoardMoveCard_NotAGitRepo_FallsBackToFilesystemMove()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "# Card");
        _ws.WriteFile(Board("20-analyze/.keep"), "");

        var result = await new BoardMoveCardTool(_board, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "analyze" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_ws.RootPath, ".yamca/board/20-analyze/0007-oauth.md")), Is.True);
    }

    [Test]
    public async Task BoardMoveCard_UnknownColumn_Error()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "# Card");
        var result = await new BoardMoveCardTool(_board, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardUpdateCard_WritesContent()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "old");

        var result = await new BoardUpdateCardTool(_board).ExecuteAsync(
            Json.Parse("""{ "card": "7", "content": "new content\n- [x] done" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var text = await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, ".yamca/board/10-idea/0007-oauth.md"));
        Assert.That(text, Is.EqualTo("new content\n- [x] done"));
    }

    [Test]
    public void PermissionDefaults_ReadsAllow_MutationsAsk()
    {
        Assert.That(new BoardListTool(_board).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetCardTool(_board).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetStepInstructionsTool(_board).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardMoveCardTool(_board, _git).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateCardTool(_board).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }

    private async Task InitRepoAndCommitAll()
    {
        await RunGitCapture("init", "-b", "main");
        await RunGitCapture("config", "user.email", "test@example.com");
        await RunGitCapture("config", "user.name", "Yamca Test");
        await RunGitCapture("add", ".");
        await RunGitCapture("commit", "-m", "initial");
    }

    private async Task<string> RunGitCapture(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _ws.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
