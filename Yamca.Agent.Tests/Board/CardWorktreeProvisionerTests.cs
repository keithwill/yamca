using System.Diagnostics;
using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Git;
using Yamca.Agent.Storage;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class CardWorktreeProvisionerTests
{
    private string _root = null!;
    private GitService _git = null!;
    private CardWorktreeProvisioner _provisioner = null!;
    private BoardStore _boardStore = null!;

    [SetUp]
    public async Task SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _root = Path.Combine(baseDir, "yamca-tests", "prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService();

        await RunGit("init", "-b", "main");
        await RunGit("config", "user.email", "test@example.com");
        await RunGit("config", "user.name", "Yamca Test");
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        await RunGit("add", ".");
        await RunGit("commit", "-m", "initial");

        var workspace = new WorkspaceImpl(_root, _root);
        _boardStore = new BoardStore(new YamcaStore(filePath: null));
        _provisioner = new CardWorktreeProvisioner(workspace, _git, _boardStore);
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
    public async Task ResolveWorktree_CreatesBranchAndWorktree_WhenNeitherExists()
    {
        var result = await _provisioner.ResolveWorktreeForBranchAsync("0001-fresh", CancellationToken.None);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Worktree, Is.Not.Null);
        Assert.That(result.Worktree!.Branch, Is.EqualTo("0001-fresh"));
        Assert.That(result.Worktree.BaseBranch, Is.EqualTo("main"));
        Assert.That(Directory.Exists(result.Worktree.WorktreePath), Is.True);

        var branches = await _git.ListBranchesAsync(_root, CancellationToken.None);
        Assert.That(branches, Does.Contain("0001-fresh"));
    }

    [Test]
    public async Task ResolveWorktree_ReusesLiveWorktree()
    {
        var first = await _provisioner.ResolveWorktreeForBranchAsync("0002-reuse", CancellationToken.None);
        var second = await _provisioner.ResolveWorktreeForBranchAsync("0002-reuse", CancellationToken.None);

        Assert.That(second.Error, Is.Null);
        // Reuse returns the path reported by `git worktree list --porcelain` (forward slashes),
        // while creation builds it with Path.Combine — normalize separators before comparing.
        Assert.That(Norm(second.Worktree!.WorktreePath), Is.EqualTo(Norm(first.Worktree!.WorktreePath)));
    }

    [Test]
    public async Task ResolveWorktree_AddsWorktree_ForExistingBranchWithoutWorktree()
    {
        await RunGit("branch", "0003-existing");

        var result = await _provisioner.ResolveWorktreeForBranchAsync("0003-existing", CancellationToken.None);

        Assert.That(result.Error, Is.Null);
        Assert.That(Directory.Exists(result.Worktree!.WorktreePath), Is.True);

        var worktrees = await _git.ListWorktreesAsync(_root, CancellationToken.None);
        Assert.That(worktrees.Any(w => w.Branch == "0003-existing"), Is.True);
    }

    [Test]
    public async Task ResolveWorktree_BasePathIsWorkspaceRoot()
    {
        var result = await _provisioner.ResolveWorktreeForBranchAsync("0004-base", CancellationToken.None);

        // BasePath keys the worktree back to the base chat session, so it must be the
        // session's sandbox root, not the worktree path.
        Assert.That(result.Worktree!.BasePath, Is.EqualTo(new WorkspaceImpl(_root, _root).RootPath));
    }

    [Test]
    public async Task LockCardToBranch_SetsCardBranch()
    {
        var ideaId = (await _boardStore.ReadAsync(CancellationToken.None)).FindColumn("idea")!.Id;
        var id = await _boardStore.AddCardAsync(ideaId, "Test", "Body", null, CardPriority.Normal, CancellationToken.None);
        var card = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard(id)!;

        var error = await _provisioner.LockCardToBranchAsync(card, "0001-test", CancellationToken.None);

        Assert.That(error, Is.Null);
        var reread = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard(id)!;
        Assert.That(reread.Branch, Is.EqualTo("0001-test"));
    }

    [Test]
    public async Task LockCardToBranch_ReturnsError_WhenCardMissing()
    {
        var card = new BoardCard(9, "Ghost", null, "ghost-col", "", Array.Empty<TaskItem>());

        var error = await _provisioner.LockCardToBranchAsync(card, "9-ghost", CancellationToken.None);

        Assert.That(error, Does.Contain("9"));
    }

    private static string Norm(string path) => path.Replace('\\', '/');

    private async Task RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {await p.StandardError.ReadToEndAsync()}");
    }
}
