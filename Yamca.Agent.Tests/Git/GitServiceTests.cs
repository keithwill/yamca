using NUnit.Framework;
using Yamca.Agent.Git;

namespace Yamca.Agent.Tests.Git;

[TestFixture]
public class GitServiceTests
{
    private string _root = null!;
    private GitService _svc = null!;

    [SetUp]
    public async Task SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _root = Path.Combine(baseDir, "yamca-tests", "git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _svc = new GitService();

        // Initialize a repo with an initial commit so worktree/merge ops have something to work with.
        await RunGit("init", "-b", "main");
        await RunGit("config", "user.email", "test@example.com");
        await RunGit("config", "user.name", "Yamca Test");
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        await RunGit("add", ".");
        await RunGit("commit", "-m", "initial");
    }

    [TearDown]
    public void TearDown()
    {
        // Loosen read-only bits on .git pack files that block recursive delete on Windows.
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task IsGitRepoAsync_returns_true_for_initialized_root()
    {
        Assert.That(await _svc.IsGitRepoAsync(_root, CancellationToken.None), Is.True);
    }

    [Test]
    public async Task GetCurrentBranchAsync_returns_main()
    {
        Assert.That(await _svc.GetCurrentBranchAsync(_root, CancellationToken.None), Is.EqualTo("main"));
    }

    [Test]
    public async Task GetRepoRootAsync_ReturnsToplevel_FromRepoRoot()
    {
        var root = await _svc.GetRepoRootAsync(_root, CancellationToken.None);
        Assert.That(Norm(root), Is.EqualTo(Norm(_root)).IgnoreCase);
    }

    [Test]
    public async Task GetRepoRootAsync_ReturnsRepoRoot_FromSubdirectory()
    {
        var sub = Path.Combine(_root, "src", "feature");
        Directory.CreateDirectory(sub);

        var root = await _svc.GetRepoRootAsync(sub, CancellationToken.None);

        // The whole point of the fix: a subdirectory still resolves to the repository top-level.
        Assert.That(Norm(root), Is.EqualTo(Norm(_root)).IgnoreCase);
    }

    [Test]
    public async Task GetRepoRootAsync_ReturnsNull_OutsideRepo()
    {
        var outside = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "yamca-tests", "norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.That(await _svc.GetRepoRootAsync(outside, CancellationToken.None), Is.Null);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best-effort */ }
        }
    }

    // git rev-parse --show-toplevel emits forward slashes (and may differ in drive-letter casing)
    // on Windows; normalize both sides before comparing.
    private static string? Norm(string? path) =>
        path is null ? null : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [Test]
    public async Task CreateWorktree_then_remove_works_for_new_branch()
    {
        var wtPath = Path.Combine(_root, ".yamca", "worktrees", "feature-x");
        var add = await _svc.CreateWorktreeAsync(_root, wtPath, "feature/x", isNewBranch: true, CancellationToken.None);
        Assert.That(add.Ok, Is.True, add.Stderr);
        Assert.That(Directory.Exists(wtPath), Is.True);

        var rm = await _svc.RemoveWorktreeAsync(_root, wtPath, force: true, CancellationToken.None);
        Assert.That(rm.Ok, Is.True, rm.Stderr);

        var del = await _svc.DeleteBranchAsync(_root, "feature/x", force: true, CancellationToken.None);
        Assert.That(del.Ok, Is.True, del.Stderr);
    }

    [Test]
    public async Task CheckRefFormat_rejects_invalid_names()
    {
        Assert.That(await _svc.CheckRefFormatAsync("ok/branch", CancellationToken.None), Is.True);
        Assert.That(await _svc.CheckRefFormatAsync("bad branch", CancellationToken.None), Is.False);
        Assert.That(await _svc.CheckRefFormatAsync("..bad", CancellationToken.None), Is.False);
    }

    [Test]
    public async Task AddOrphanWorktreeAsync_CreatesParentlessBranch_InWorktree()
    {
        var wtPath = Path.Combine(_root, ".yamca", "board");

        var add = await _svc.AddOrphanWorktreeAsync(_root, wtPath, "yamca-board", CancellationToken.None);
        Assert.That(add.Ok, Is.True, add.Stderr);
        Assert.That(Directory.Exists(wtPath), Is.True);

        // Seed a file and commit it as the orphan's root commit.
        File.WriteAllText(Path.Combine(wtPath, "card.md"), "hi\n");
        var commit = await _svc.CommitAllAsync(wtPath, "seed", CancellationToken.None);
        Assert.That(commit.Ok, Is.True, commit.Stderr);

        // Exactly one commit, and it has no parent — a history disconnected from the code branches.
        var count = (await RunGitCapture("rev-list", "--count", "yamca-board")).Trim();
        Assert.That(count, Is.EqualTo("1"));
        var parents = (await RunGitCapture("log", "yamca-board", "--format=%P")).Trim();
        Assert.That(parents, Is.Empty, "the orphan root commit must have no parent");
    }

    [Test]
    public async Task CommitAllAsync_Commits_AndIsNoOpWhenClean()
    {
        var wtPath = Path.Combine(_root, ".yamca", "board");
        await _svc.AddOrphanWorktreeAsync(_root, wtPath, "yamca-board", CancellationToken.None);

        File.WriteAllText(Path.Combine(wtPath, "card.md"), "hi\n");
        var first = await _svc.CommitAllAsync(wtPath, "add card", CancellationToken.None);
        Assert.That(first.Ok, Is.True, first.Stderr);

        var before = (await RunGitCapture("rev-list", "--count", "yamca-board")).Trim();
        var noop = await _svc.CommitAllAsync(wtPath, "nothing changed", CancellationToken.None);
        Assert.That(noop.Ok, Is.True, "a clean tree is a benign no-op");
        var after = (await RunGitCapture("rev-list", "--count", "yamca-board")).Trim();
        Assert.That(after, Is.EqualTo(before), "a clean tree must not produce a new commit");
    }

    [Test]
    public async Task RevParseHeadAsync_ReturnsShaAndBranch()
    {
        var head = await _svc.RevParseHeadAsync(_root, CancellationToken.None);
        Assert.That(head, Is.Not.Null);
        Assert.That(head!.Value.Sha, Is.Not.Empty);
        Assert.That(head.Value.Branch, Is.EqualTo("main"));
    }

    [Test]
    public async Task RevParseHeadAsync_ReturnsNull_OnUnbornHead()
    {
        var empty = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "yamca-tests", "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            await RunGitInAsync(empty, "init", "-b", "main");
            Assert.That(await _svc.RevParseHeadAsync(empty, CancellationToken.None), Is.Null);
        }
        finally
        {
            try { Directory.Delete(empty, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task BranchExistsAsync_TrueForExisting_FalseOtherwise()
    {
        Assert.That(await _svc.BranchExistsAsync(_root, "main", CancellationToken.None), Is.True);
        Assert.That(await _svc.BranchExistsAsync(_root, "no-such-branch", CancellationToken.None), Is.False);
    }

    [Test]
    public async Task GetFileCreatedAt_And_LastModified_ReturnDates()
    {
        await CommitFile("card.md", "v1\n");

        var created = await _svc.GetFileCreatedAtAsync(_root, "card.md", CancellationToken.None);
        var modified = await _svc.GetFileLastModifiedAtAsync(_root, "card.md", CancellationToken.None);

        Assert.That(created, Is.Not.Null);
        Assert.That(modified, Is.Not.Null);
    }

    [Test]
    public async Task GetFileCreatedAt_UncommittedFile_IsNull()
    {
        File.WriteAllText(Path.Combine(_root, "loose.md"), "x");
        Assert.That(await _svc.GetFileCreatedAtAsync(_root, "loose.md", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task GetFileHistory_FollowsRename()
    {
        await CommitFile("card.md", "v1\n");
        await RunGit("mv", "card.md", "renamed.md");
        await RunGit("commit", "-m", "rename card");

        var history = await _svc.GetFileHistoryAsync(_root, "renamed.md", CancellationToken.None);

        Assert.That(history.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(history[0].Subject, Is.EqualTo("rename card"));
        Assert.That(history[0].Sha, Is.Not.Empty);
    }

    [Test]
    public async Task HasUncommittedChanges_TrueForUntracked_FalseAfterCommit()
    {
        File.WriteAllText(Path.Combine(_root, "card.md"), "x\n");
        Assert.That(await _svc.HasUncommittedChangesAsync(_root, "card.md", CancellationToken.None), Is.True);

        await CommitFile("card.md", "x\n");
        Assert.That(await _svc.HasUncommittedChangesAsync(_root, "card.md", CancellationToken.None), Is.False);
    }

    [Test]
    public async Task GetWorktreeDiffStat_CountsCommittedAndUncommitted_AgainstBase()
    {
        // Branch off main into a linked worktree.
        var wtPath = Path.Combine(_root, ".yamca", "worktrees", "feature");
        var add = await _svc.CreateWorktreeAsync(_root, wtPath, "feature/stat", isNewBranch: true, CancellationToken.None);
        Assert.That(add.Ok, Is.True, add.Stderr);

        // One committed change on the branch: append two lines to README.
        await File.WriteAllTextAsync(Path.Combine(wtPath, "README.md"), "hello\nadded-1\nadded-2\n");
        await RunGitInAsync(wtPath, "commit", "-am", "extend readme");

        // One uncommitted new file (untracked) plus an uncommitted edit to a tracked file.
        await File.WriteAllTextAsync(Path.Combine(wtPath, "new.txt"), "fresh\n");
        await File.WriteAllTextAsync(Path.Combine(wtPath, "README.md"), "hello\nadded-1\nadded-2\nadded-3\n");

        var stat = await _svc.GetWorktreeDiffStatAsync(wtPath, "main", CancellationToken.None);

        Assert.That(stat, Is.Not.Null);
        // README diff vs the fork point: 3 lines added, 0 removed. new.txt is untracked so it does
        // not appear in `git diff` against a commit — only in the uncommitted file count.
        Assert.That(stat!.FilesChanged, Is.EqualTo(1));
        Assert.That(stat.Insertions, Is.EqualTo(3));
        Assert.That(stat.Deletions, Is.EqualTo(0));
        // Two files with pending changes: the README edit and the untracked new.txt.
        Assert.That(stat.UncommittedFiles, Is.EqualTo(2));

        await _svc.RemoveWorktreeAsync(_root, wtPath, force: true, CancellationToken.None);
    }

    [Test]
    public async Task GetWorktreeDiffStat_IsEmpty_ForCleanWorktreeAtBase()
    {
        var wtPath = Path.Combine(_root, ".yamca", "worktrees", "clean");
        await _svc.CreateWorktreeAsync(_root, wtPath, "feature/clean", isNewBranch: true, CancellationToken.None);

        var stat = await _svc.GetWorktreeDiffStatAsync(wtPath, "main", CancellationToken.None);

        Assert.That(stat, Is.Not.Null);
        Assert.That(stat!.IsEmpty, Is.True);

        await _svc.RemoveWorktreeAsync(_root, wtPath, force: true, CancellationToken.None);
    }

    private async Task CommitFile(string relative, string content)
    {
        File.WriteAllText(Path.Combine(_root, relative), content);
        await RunGit("add", relative);
        await RunGit("commit", "-m", $"add {relative}");
    }

    private async Task RunGit(params string[] args) => await RunGitCapture(args);

    // Runs git in an arbitrary directory (the other helpers are pinned to _root).
    private static async Task RunGitInAsync(string dir, params string[] args)
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
        await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
    }

    private async Task<string> RunGitCapture(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
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
