using Yamca.Agent.Git;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Board;

/// <summary>Outcome of resolving a worktree for a card branch: exactly one of
/// <see cref="Worktree"/> or <see cref="Error"/> is non-null.</summary>
public sealed record ProvisionResult(WorktreeInfo? Worktree, string? Error)
{
    public static ProvisionResult Ok(WorktreeInfo worktree) => new(worktree, null);
    public static ProvisionResult Fail(string error) => new(null, error);
}

/// <summary>
/// Provisions the per-card branch worktree used by board step runs, shared by the
/// interactive Run Step flow (Board.razor) and the orchestrator's headless dispatch.
/// Pure agent-layer service: errors are returned, not surfaced to any UI.
/// </summary>
public sealed class CardWorktreeProvisioner
{
    private readonly IWorkspace _workspace;
    private readonly GitService _git;
    private readonly BoardStore _boardStore;

    public CardWorktreeProvisioner(IWorkspace workspace, GitService git, BoardStore boardStore)
    {
        _workspace = workspace;
        _git = git;
        _boardStore = boardStore;
    }

    /// <summary>Resolve a usable worktree for a branch, creating whatever is missing:
    /// a live worktree already checked out on the branch is reused; an existing branch
    /// without a worktree gets one added; when neither exists (deleted/merged, or a
    /// brand-new card branch) both branch and worktree are created.</summary>
    public async Task<ProvisionResult> ResolveWorktreeForBranchAsync(string branch, CancellationToken ct)
    {
        // Worktrees and git operations are repo-scoped, so they anchor at the repository root.
        // BasePath, however, stays the session's sandbox root: it doubles as the key that ties a
        // worktree back to the base chat session (see ChatSessionPanel.IsBranchOpBusy).
        var repoRoot = _workspace.RepositoryRoot;
        var baseBranch = await _git.GetCurrentBranchAsync(repoRoot, ct) ?? "";

        var worktrees = await _git.ListWorktreesAsync(repoRoot, ct);
        var match = worktrees.FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.Ordinal));
        if (match.Path is not null)
            return ProvisionResult.Ok(new WorktreeInfo(branch, baseBranch, match.Path, _workspace.RootPath));

        var path = WorktreePathForBranch(repoRoot, branch);
        if (!Directory.Exists(path))
        {
            var branches = await _git.ListBranchesAsync(repoRoot, ct);
            var branchExists = branches.Any(b => string.Equals(b, branch, StringComparison.Ordinal));
            var r = await _git.CreateWorktreeAsync(repoRoot, path, branch, isNewBranch: !branchExists, ct);
            if (!r.Ok)
                return ProvisionResult.Fail($"Could not open worktree for '{branch}': {r.Stderr.Trim()}");
        }
        return ProvisionResult.Ok(new WorktreeInfo(branch, baseBranch, path, _workspace.RootPath));
    }

    /// <summary>Bind a card to its branch by writing the <c>branch:</c> frontmatter. The board is
    /// plain on-disk state, so this is just a card-file write under the board lock. Returns null
    /// on success or an error message on failure.</summary>
    public Task<string?> LockCardToBranchAsync(BoardCard card, string branch, CancellationToken ct)
    {
        return _boardStore.MutateAsync<string?>(async _ =>
        {
            try
            {
                var raw = await File.ReadAllTextAsync(card.AbsolutePath, ct);
                await File.WriteAllTextAsync(card.AbsolutePath, BoardService.WithBranch(raw, branch), ct);
                return null;
            }
            catch (Exception ex)
            {
                return $"Could not bind card to '{branch}': {ex.Message}";
            }
        }, ct);
    }

    /// <summary>The canonical on-disk location for a branch's worktree.</summary>
    public static string WorktreePathForBranch(string repoRoot, string branch) =>
        Path.Combine(repoRoot, ".yamca", "worktrees", Sanitize(branch));

    public static string Sanitize(string branch) => branch.Replace('/', '-').Replace('\\', '-');
}
