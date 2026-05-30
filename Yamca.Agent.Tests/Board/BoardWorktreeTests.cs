using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Git;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardWorktreeTests
{
    private string _root = null!;
    private GitService _git = null!;
    private BoardWorktree _bw = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "yamca-tests", "bw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService();

        await RunGit(_root, "init", "-b", "main");
        await RunGit(_root, "config", "user.email", "test@example.com");
        await RunGit(_root, "config", "user.name", "Yamca Test");
        await RunGit(_root, "commit", "--allow-empty", "-m", "initial");

        _bw = new BoardWorktree(new WorkspaceImpl(_root), _git);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task EnsureAsync_CreatesOrphanBranch_WithParentlessRootCommit()
    {
        var path = await _bw.EnsureAsync(CancellationToken.None);

        Assert.That(path, Is.EqualTo(Path.Combine(_root, ".yamca", "board")));
        Assert.That(Directory.Exists(path), Is.True);
        Assert.That(await _git.BranchExistsAsync(_root, BoardWorktree.BranchName, CancellationToken.None), Is.True);

        // Exactly one (seed) commit, and it has no parent — disconnected from the code history.
        var count = (await RunGitCapture(path, "rev-list", "--count", BoardWorktree.BranchName)).Trim();
        Assert.That(count, Is.EqualTo("1"));
        var parents = (await RunGitCapture(path, "log", BoardWorktree.BranchName, "--format=%P")).Trim();
        Assert.That(parents, Is.Empty, "the orphan root commit must have no parent");
    }

    [Test]
    public async Task EnsureAsync_SeedsDefaultColumns()
    {
        var path = await _bw.EnsureAsync(CancellationToken.None);

        foreach (var (dir, _) in BoardService.DefaultColumns)
        {
            Assert.That(Directory.Exists(Path.Combine(path, dir)), Is.True, dir);
            Assert.That(File.Exists(Path.Combine(path, dir, BoardService.InstructionsFileName)), Is.True, dir);
        }
    }

    [Test]
    public async Task EnsureAsync_FreshInstance_ReusesExistingWorktree()
    {
        await _bw.EnsureAsync(CancellationToken.None);

        // A second BoardWorktree over the same repo must reuse the registered worktree, not add a new one.
        var other = new BoardWorktree(new WorkspaceImpl(_root), _git);
        var path = await other.EnsureAsync(CancellationToken.None);

        Assert.That(path, Is.EqualTo(Path.Combine(_root, ".yamca", "board")));
        var list = await RunGitCapture(_root, "worktree", "list", "--porcelain");
        Assert.That(list.Split('\n').Count(l => l.Contains(BoardWorktree.BranchName)), Is.EqualTo(1),
            "exactly one worktree should be registered on the board branch");
    }

    [Test]
    public async Task MutateAsync_SerializesConcurrentWriters()
    {
        await _bw.EnsureAsync(CancellationToken.None);

        var inside = 0;
        var maxConcurrent = 0;
        async Task<bool> Body(string _)
        {
            var now = Interlocked.Increment(ref inside);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            return true;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _bw.MutateAsync(Body, CancellationToken.None)));

        Assert.That(maxConcurrent, Is.EqualTo(1), "MutateAsync must serialize board writers");
    }

    [Test]
    public async Task MutateAsync_CommitsEachMutation()
    {
        var path = await _bw.EnsureAsync(CancellationToken.None);
        var before = int.Parse((await RunGitCapture(path, "rev-list", "--count", BoardWorktree.BranchName)).Trim());

        await _bw.MutateAsync(async board =>
        {
            await File.WriteAllTextAsync(Path.Combine(board, "10-idea", "0001-x.md"), "# X");
            return await _git.CommitAllAsync(board, "board: add #0001", CancellationToken.None);
        }, CancellationToken.None);

        var after = int.Parse((await RunGitCapture(path, "rev-list", "--count", BoardWorktree.BranchName)).Trim());
        Assert.That(after, Is.EqualTo(before + 1));
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
