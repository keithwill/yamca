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
    private BoardWorktree _boardWorktree = null!;
    private string _boardPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _ws = new TempWorkspace();
        _board = new BoardService();
        _git = new GitService();

        // The board tools resolve and commit through a real board worktree, so the workspace must be
        // a git repo. Initialize it with one commit (so the code HEAD exists for the association
        // stamp), then bootstrap the board worktree + default columns on the yamca-board branch.
        await RunGit(_ws.RootPath, "init", "-b", "main");
        await RunGit(_ws.RootPath, "config", "user.email", "test@example.com");
        await RunGit(_ws.RootPath, "config", "user.name", "Yamca Test");
        await RunGit(_ws.RootPath, "commit", "--allow-empty", "-m", "initial");

        _boardWorktree = new BoardWorktree(_ws.Workspace, _git);
        _boardPath = await _boardWorktree.EnsureAsync(CancellationToken.None);
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

    // Card paths are relative to the board worktree (which is mounted at <root>/.yamca/board).
    private string Board(string relative) => $".yamca/board/{relative}";

    [Test]
    public async Task BoardList_FormatsColumnsCardsAndProgress()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\ntitle: Add OAuth\nbranch: feat/oauth\n---\n- [x] a\n- [ ] b");

        var result = await new BoardListTool(_board, _boardWorktree).ExecuteAsync(
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

        var result = await new BoardGetCardTool(_board, _boardWorktree).ExecuteAsync(
            Json.Parse("""{ "card": "7" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("# Body"));
        Assert.That(result.Content, Does.Contain("id: 7"));
    }

    [Test]
    public async Task BoardGetCard_UnknownCard_Error()
    {
        var result = await new BoardGetCardTool(_board, _boardWorktree).ExecuteAsync(
            Json.Parse("""{ "card": "999" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetStepInstructions_ReturnsContent_AndNoteWhenMissing()
    {
        // Overwrite a work column's seeded instructions, and use a resting column (done, seeded empty)
        // for the missing case.
        _ws.WriteFile(Board("30-implement/instructions.md"), "Write the code.");

        var present = await new BoardGetStepInstructionsTool(_board, _boardWorktree).ExecuteAsync(
            Json.Parse("""{ "column": "implement" }"""), Ctx(), CancellationToken.None);
        Assert.That(present.IsError, Is.False, present.Content);
        Assert.That(present.Content, Does.Contain("Write the code."));

        var missing = await new BoardGetStepInstructionsTool(_board, _boardWorktree).ExecuteAsync(
            Json.Parse("""{ "column": "done" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.False);
        Assert.That(missing.Content, Does.Contain("no instructions"));
    }

    [Test]
    public async Task BoardMoveCard_CommitsToBoardBranch_AndStampsCodeCommit()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "---\nid: 7\ntitle: OAuth\n---\n# Card");
        await _git.CommitAllAsync(_boardPath, "seed card", CancellationToken.None);

        var result = await new BoardMoveCardTool(_board, _boardWorktree, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "analyze" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_boardPath, "20-analyze", "0007-oauth.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(_boardPath, "10-idea", "0007-oauth.md")), Is.False);

        // The move is committed to the board branch, so the board worktree is clean afterwards.
        var status = await RunGitCapture(_boardPath, "status", "--porcelain");
        Assert.That(status.Trim(), Is.Empty, "board worktree should be clean after the committed move");

        // Association stamp: the board commit message carries the Code: trailer and the moved card's
        // frontmatter records the code commit it corresponds to.
        var message = await RunGitCapture(_boardPath, "log", "-1", "--format=%B");
        Assert.That(message, Does.Contain("Code:"));
        var moved = await File.ReadAllTextAsync(Path.Combine(_boardPath, "20-analyze", "0007-oauth.md"));
        Assert.That(moved, Does.Contain("commit:"));
    }

    [Test]
    public async Task BoardMoveCard_UnknownColumn_Error()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "# Card");
        var result = await new BoardMoveCardTool(_board, _boardWorktree, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "to_column": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardUpdateCard_WritesContent_AndCommits()
    {
        _ws.WriteFile(Board("10-idea/0007-oauth.md"), "old");
        await _git.CommitAllAsync(_boardPath, "seed card", CancellationToken.None);

        var result = await new BoardUpdateCardTool(_board, _boardWorktree, _git).ExecuteAsync(
            Json.Parse("""{ "card": "7", "content": "new content\n- [x] done" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var text = await File.ReadAllTextAsync(Path.Combine(_boardPath, "10-idea", "0007-oauth.md"));
        Assert.That(text, Is.EqualTo("new content\n- [x] done"));

        // Committed to the board branch: the board worktree is clean.
        var status = await RunGitCapture(_boardPath, "status", "--porcelain");
        Assert.That(status.Trim(), Is.Empty, "board worktree should be clean after the committed update");
    }

    [Test]
    public void PermissionDefaults_ReadsAllow_MutationsAsk()
    {
        Assert.That(new BoardListTool(_board, _boardWorktree).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetCardTool(_board, _boardWorktree).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetStepInstructionsTool(_board, _boardWorktree).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardMoveCardTool(_board, _boardWorktree, _git).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateCardTool(_board, _boardWorktree, _git).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }

    private static async Task RunGit(string dir, params string[] args) => await RunGitCapture(dir, args);

    private static async Task<string> RunGitCapture(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
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
