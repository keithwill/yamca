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
    public async Task MoveAsync_StagesRename()
    {
        await CommitFile("card.md", "hello\n");

        var move = await _svc.MoveAsync(_root, "card.md", "moved.md", CancellationToken.None);
        Assert.That(move.Ok, Is.True, move.Stderr);
        Assert.That(File.Exists(Path.Combine(_root, "moved.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(_root, "card.md")), Is.False);

        var status = await RunGitCapture("status", "--porcelain");
        Assert.That(status.Trim(), Does.StartWith("R"));   // staged rename, not committed
    }

    [Test]
    public async Task CommitStagedPathsAsync_CommitsStagedRename_WithoutReAdding()
    {
        await CommitFile("card.md", "hello\n");

        // git mv stages both sides of the rename and removes the source from the index. Committing
        // the staged paths must then succeed WITHOUT re-running `git add` on the source — doing so
        // (as CommitPathsAsync does) fatals with "pathspec did not match", which was the promote bug.
        var move = await _svc.MoveAsync(_root, "card.md", "moved.md", CancellationToken.None);
        Assert.That(move.Ok, Is.True, move.Stderr);

        var commit = await _svc.CommitStagedPathsAsync(_root, "move card", new[] { "card.md", "moved.md" }, CancellationToken.None);
        Assert.That(commit.Ok, Is.True, commit.Stderr);

        var status = await RunGitCapture("status", "--porcelain");
        Assert.That(status.Trim(), Is.Empty, "working tree should be clean after committing the rename");
        Assert.That(File.Exists(Path.Combine(_root, "moved.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(_root, "card.md")), Is.False);
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
    public async Task CommitPaths_CommitsOnlyPathspec_LeavingOtherChangesStaged()
    {
        // An untracked card plus an unrelated staged change in the same tree.
        File.WriteAllText(Path.Combine(_root, "card.md"), "card\n");
        File.WriteAllText(Path.Combine(_root, "other.txt"), "other\n");
        await RunGit("add", "other.txt");

        var commit = await _svc.CommitPathsAsync(_root, "board: bind card", new[] { "card.md" }, CancellationToken.None);
        Assert.That(commit.Ok, Is.True, commit.Stderr);

        // The card is committed...
        Assert.That(await _svc.HasUncommittedChangesAsync(_root, "card.md", CancellationToken.None), Is.False);
        var subject = (await RunGitCapture("log", "-1", "--format=%s")).Trim();
        Assert.That(subject, Is.EqualTo("board: bind card"));

        // ...while the unrelated change is untouched (still staged, not swept into the commit).
        var status = (await RunGitCapture("status", "--porcelain", "--", "other.txt")).Trim();
        Assert.That(status, Is.EqualTo("A  other.txt"));
    }

    private async Task CommitFile(string relative, string content)
    {
        File.WriteAllText(Path.Combine(_root, relative), content);
        await RunGit("add", relative);
        await RunGit("commit", "-m", $"add {relative}");
    }

    private async Task RunGit(params string[] args) => await RunGitCapture(args);

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
