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

    private async Task RunGit(params string[] args)
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
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {await p.StandardError.ReadToEndAsync()}");
    }
}
