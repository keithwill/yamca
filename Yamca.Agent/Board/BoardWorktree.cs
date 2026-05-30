using Microsoft.Extensions.Logging;
using Yamca.Agent.Git;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Board;

/// <summary>Owns the location and bootstrap of the single, repo-anchored board worktree.
///
/// The board lives on a dedicated <em>orphan</em> branch (<see cref="BranchName"/>) — a history
/// with no shared ancestor with the code branches — checked out as a linked worktree at
/// <c>&lt;RepositoryRoot&gt;/.yamca/board</c>. Because location is resolved from the injected
/// <em>root</em> <see cref="IWorkspace"/> (the true main-repo top-level discovered at startup) and
/// not from any per-session workspace, every chat session and the board UI read and write the one
/// canonical board regardless of which code branch they are on.
///
/// All mutations funnel through <see cref="MutateAsync{T}"/>, which serializes them under a
/// process-wide semaphore (the single worktree replaces the old per-branch isolation, so concurrent
/// writers must take turns). Reads are lock-free filesystem reads against <see cref="EnsureAsync"/>'s
/// path. Adequate for a local, single-user tool.</summary>
public sealed class BoardWorktree
{
    /// <summary>The orphan branch the board lives on.</summary>
    public const string BranchName = "yamca-board";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly IWorkspace _workspace;
    private readonly GitService _git;
    private readonly ILogger<BoardWorktree> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ensuredPath;
    // Null = not yet discovered; empty string = no remote configured (skip sync).
    private string? _cachedRemote;

    public BoardWorktree(IWorkspace workspace, GitService git, ILogger<BoardWorktree> logger)
    {
        _workspace = workspace;
        _git = git;
        _logger = logger;
    }

    /// <summary>Resolve the board worktree path (<c>&lt;RepositoryRoot&gt;/.yamca/board</c>),
    /// creating the orphan branch + worktree and seeding the default columns on first use.
    /// Idempotent and cached after first success.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (_ensuredPath is not null) return _ensuredPath;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await EnsureCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Run a board mutation under the process-wide write lock, ensuring the worktree first.
    /// <paramref name="action"/> receives the board worktree path; it is the only place board files
    /// are written and committed. When a remote is configured, the board branch is rebased onto the
    /// latest remote state before the action runs, and pushed afterwards.</summary>
    public async Task<T> MutateAsync<T>(Func<string, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = await EnsureCoreAsync(ct).ConfigureAwait(false);
            var remote = await GetCachedRemoteAsync(ct).ConfigureAwait(false);
            if (remote is not null)
                await TryPullRebaseAsync(path, remote, ct).ConfigureAwait(false);
            var result = await action(path).ConfigureAwait(false);
            if (remote is not null)
                await TryPushAsync(path, remote, ct).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lock-taking convenience for mutations with no return value.</summary>
    public Task MutateAsync(Func<string, Task> action, CancellationToken ct)
        => MutateAsync(async path => { await action(path).ConfigureAwait(false); return true; }, ct);

    // Must be called with _gate held (EnsureAsync and MutateAsync both do): the semaphore is not
    // reentrant, so locking here would deadlock when called from inside MutateAsync.
    private async Task<string> EnsureCoreAsync(CancellationToken ct)
    {
        if (_ensuredPath is not null) return _ensuredPath;

        var repoRoot = _workspace.RepositoryRoot;
        var boardPath = Path.Combine(repoRoot, ".yamca", "board");

        if (!await _git.BranchExistsAsync(repoRoot, BranchName, ct).ConfigureAwait(false))
        {
            // First run: create the orphan branch in the worktree, seed columns, and commit the
            // board's root commit (parentless — disconnected from code history).
            var add = await _git.AddOrphanWorktreeAsync(repoRoot, boardPath, BranchName, ct).ConfigureAwait(false);
            if (!add.Ok)
                throw new InvalidOperationException($"Could not create the board worktree on '{BranchName}': {add.Stderr.Trim()}");

            await SeedDefaultColumnsAsync(boardPath, ct).ConfigureAwait(false);

            var commit = await _git.CommitAllAsync(boardPath, "board: initialize board on orphan branch", ct).ConfigureAwait(false);
            if (!commit.Ok)
                throw new InvalidOperationException($"Could not commit the initial board: {commit.Stderr.Trim()}");

            // Share the seeded board immediately so other users can clone it.
            var remote = await GetCachedRemoteAsync(ct).ConfigureAwait(false);
            if (remote is not null)
                await TryPushAsync(boardPath, remote, ct).ConfigureAwait(false);
        }
        else if (!await IsWorktreeRegisteredAsync(repoRoot, boardPath, ct).ConfigureAwait(false))
        {
            // The branch exists but its worktree is not mounted at the canonical path (e.g. a fresh
            // checkout). Mount it.
            var add = await _git.CreateWorktreeAsync(repoRoot, boardPath, BranchName, isNewBranch: false, ct).ConfigureAwait(false);
            if (!add.Ok)
                throw new InvalidOperationException($"Could not mount the board worktree at '{boardPath}': {add.Stderr.Trim()}");
        }

        _ensuredPath = boardPath;
        return boardPath;
    }

    // Returns the remote name to use for board sync, or null when no remote is configured.
    // Result is cached after the first call — remotes do not change at runtime.
    private async Task<string?> GetCachedRemoteAsync(CancellationToken ct)
    {
        if (_cachedRemote is not null) return _cachedRemote == "" ? null : _cachedRemote;
        var remote = await _git.GetDefaultRemoteAsync(_workspace.RepositoryRoot, ct).ConfigureAwait(false);
        _cachedRemote = remote ?? "";
        return remote;
    }

    // Fetch the board branch from remote then rebase local onto it, so any subsequent push is
    // guaranteed to be fast-forward. If rebase fails (genuine conflict), abort and throw so the
    // mutation is not applied in a conflicted state.
    private async Task TryPullRebaseAsync(string boardPath, string remote, CancellationToken ct)
    {
        var repoRoot = _workspace.RepositoryRoot;
        await _git.FetchAsync(repoRoot, remote, BranchName, ct).ConfigureAwait(false);

        if (!await _git.RemoteTrackingBranchExistsAsync(repoRoot, remote, BranchName, ct).ConfigureAwait(false))
            return; // Remote branch not yet created — nothing to rebase onto.

        var behind = await _git.CountCommitsAheadAsync(boardPath, "HEAD", $"{remote}/{BranchName}", ct).ConfigureAwait(false);
        if (behind == 0) return;

        var rebase = await _git.RebaseAsync(boardPath, $"{remote}/{BranchName}", ct).ConfigureAwait(false);
        if (!rebase.Ok)
        {
            await _git.AbortRebaseAsync(boardPath, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Board is out of sync — could not rebase before applying change. Try again. ({rebase.Stderr.Trim()})");
        }
    }

    // Push the board branch to remote. Uses --set-upstream on the very first push.
    // Push failures are logged as warnings but never thrown — the local commit always stands.
    private async Task TryPushAsync(string boardPath, string remote, CancellationToken ct)
    {
        var repoRoot = _workspace.RepositoryRoot;
        var setUpstream = !await _git.RemoteTrackingBranchExistsAsync(repoRoot, remote, BranchName, ct).ConfigureAwait(false);
        var push = await _git.PushAsync(boardPath, remote, BranchName, setUpstream, ct).ConfigureAwait(false);
        if (!push.Ok)
            _logger.LogWarning("Board push to {Remote}/{Branch} failed: {Error}", remote, BranchName, push.Stderr.Trim());
    }

    private async Task<bool> IsWorktreeRegisteredAsync(string repoRoot, string boardPath, CancellationToken ct)
    {
        var target = Normalize(boardPath);
        var worktrees = await _git.ListWorktreesAsync(repoRoot, ct).ConfigureAwait(false);
        return worktrees.Any(w => string.Equals(Normalize(w.Path), target, PathComparison));
    }

    /// <summary>Restore the board to the default column layout. Cards already in a default column
    /// stay in place; cards in unknown columns move to the initial (idea) column. Pass
    /// <paramref name="wipe"/> to delete all cards instead.</summary>
    public Task<ReinitResult> ReinitAsync(bool wipe, CancellationToken ct)
        => MutateAsync(async boardRoot =>
        {
            var result = await ReinitCoreAsync(boardRoot, wipe, ct).ConfigureAwait(false);
            var msg = BuildReinitCommitMessage(result, wipe);
            await _git.CommitAllAsync(boardRoot, msg, ct).ConfigureAwait(false);
            return result;
        }, ct);

    private static async Task<ReinitResult> ReinitCoreAsync(string boardRoot, bool wipe, CancellationToken ct)
    {
        var snapshot = new BoardService().Read(boardRoot);

        var defaultDirNames = BoardService.DefaultColumns.Select(c => c.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ideaDir = BoardService.DefaultColumns[0].Dir;

        // Count what will change before seeding so the summary is accurate.
        int columnsCreated = 0;
        int instructionsRestored = 0;
        foreach (var (dir, instructions) in BoardService.DefaultColumns)
        {
            var columnDir = Path.Combine(boardRoot, dir);
            if (!Directory.Exists(columnDir))
            {
                columnsCreated++;
            }
            else
            {
                var instrPath = Path.Combine(columnDir, BoardService.InstructionsFileName);
                var expected = instructions ?? "";
                string current;
                try { current = await File.ReadAllTextAsync(instrPath, ct).ConfigureAwait(false); }
                catch (IOException) { current = ""; }
                if (!string.Equals(current, expected, StringComparison.Ordinal))
                    instructionsRestored++;
            }
        }

        // Restore the column structure (idempotent — overwrites instructions.md unconditionally).
        await SeedDefaultColumnsAsync(boardRoot, ct).ConfigureAwait(false);

        // Relocate or delete cards.
        int cardsPreserved = 0, cardsMoved = 0, cardsWiped = 0;
        foreach (var card in snapshot.AllCards)
        {
            if (wipe)
            {
                File.Delete(card.AbsolutePath);
                cardsWiped++;
            }
            else if (defaultDirNames.Contains(card.ColumnDirectory))
            {
                cardsPreserved++;
            }
            else
            {
                var dest = Path.Combine(boardRoot, ideaDir, card.FileName);
                if (File.Exists(dest))
                    dest = Path.Combine(boardRoot, ideaDir,
                        Path.GetFileNameWithoutExtension(card.FileName) + "-moved.md");
                File.Move(card.AbsolutePath, dest);
                cardsMoved++;
            }
        }

        // Clean up non-default column directories that are now empty (or instructions-only).
        foreach (var dir in Directory.EnumerateDirectories(boardRoot))
        {
            var dirName = Path.GetFileName(dir);
            if (!BoardService.TryParseColumnDir(dirName, out _, out _)) continue;
            if (defaultDirNames.Contains(dirName)) continue;

            var remaining = Directory.EnumerateFiles(dir)
                .Where(f => !string.Equals(Path.GetFileName(f),
                    BoardService.InstructionsFileName, StringComparison.OrdinalIgnoreCase))
                .Any();
            if (!remaining) Directory.Delete(dir, recursive: true);
        }

        return new ReinitResult(columnsCreated, instructionsRestored, cardsPreserved, cardsMoved, cardsWiped);
    }

    private static string BuildReinitCommitMessage(ReinitResult r, bool wipe)
    {
        var detail = $"Columns created: {r.ColumnsCreated}, instructions restored: {r.InstructionsRestored}, " +
                     $"cards preserved: {r.CardsPreserved}, cards moved to idea: {r.CardsMoved}" +
                     (wipe && r.CardsWiped > 0 ? $", cards wiped: {r.CardsWiped}" : "");
        return $"board: reinit — restored default columns\n\n{detail}";
    }

    internal static async Task SeedDefaultColumnsAsync(string boardPath, CancellationToken ct)
    {
        foreach (var (dir, instructions) in BoardService.DefaultColumns)
        {
            var columnDir = Path.Combine(boardPath, dir);
            Directory.CreateDirectory(columnDir);
            // Every column carries an instructions.md (empty for resting columns) so its directory
            // survives in git, which does not track empty directories.
            var instrPath = Path.Combine(columnDir, BoardService.InstructionsFileName);
            await File.WriteAllTextAsync(instrPath, instructions ?? "", ct).ConfigureAwait(false);
        }
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
