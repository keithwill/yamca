using Yamca.Agent.Workspace;

namespace Yamca.Agent.Board;

/// <summary>Owns the location and bootstrap of the single, repo-anchored dev board.
///
/// The board is a plain, uncommitted directory at <c>&lt;RepositoryRoot&gt;/.yamca/board</c> — a
/// personal scratchpad of the current user's work, gitignored and never tracked, committed, or
/// pushed. Because location is resolved from the injected <em>root</em> <see cref="IWorkspace"/>
/// (the true main-repo top-level discovered at startup) and not from any per-session workspace,
/// every chat session and the board UI read and write the one canonical board regardless of which
/// code branch — or code worktree — they are on.
///
/// All mutations funnel through <see cref="MutateAsync{T}"/>, which serializes them under a
/// process-wide semaphore so concurrent writers (board UI + chat sessions) take turns rather than
/// interleaving file moves. Reads are lock-free filesystem reads against <see cref="EnsureAsync"/>'s
/// path. Adequate for a local, single-user tool.</summary>
public sealed class BoardStore
{
    private readonly IWorkspace _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ensuredPath;

    public BoardStore(IWorkspace workspace)
    {
        _workspace = workspace;
    }

    /// <summary>Resolve the board directory (<c>&lt;RepositoryRoot&gt;/.yamca/board</c>), creating it
    /// and seeding the default columns on first use. Idempotent and cached after first success.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (_ensuredPath is not null) return _ensuredPath;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await EnsureCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Run a board mutation under the process-wide write lock, ensuring the board directory
    /// first. <paramref name="action"/> receives the board path; it is the only place board files are
    /// written. The board is plain on-disk state — a write to the file <em>is</em> the mutation; there
    /// is no commit or remote sync.</summary>
    public async Task<T> MutateAsync<T>(Func<string, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action(await EnsureCoreAsync(ct).ConfigureAwait(false)).ConfigureAwait(false); }
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

        var boardPath = Path.Combine(_workspace.RepositoryRoot, ".yamca", "board");
        if (!Directory.Exists(boardPath))
        {
            Directory.CreateDirectory(boardPath);
            await SeedDefaultColumnsAsync(boardPath, ct).ConfigureAwait(false);
        }

        _ensuredPath = boardPath;
        return boardPath;
    }

    /// <summary>Restore the board to the default column layout. Cards already in a default column
    /// stay in place; cards in unknown columns move to the initial (idea) column. Pass
    /// <paramref name="wipe"/> to delete all cards instead.</summary>
    public Task<ReinitResult> ReinitAsync(bool wipe, CancellationToken ct)
        => MutateAsync(boardRoot => ReinitCoreAsync(boardRoot, wipe, ct), ct);

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

    internal static async Task SeedDefaultColumnsAsync(string boardPath, CancellationToken ct)
    {
        foreach (var (dir, instructions) in BoardService.DefaultColumns)
        {
            var columnDir = Path.Combine(boardPath, dir);
            Directory.CreateDirectory(columnDir);
            // Every column carries an instructions.md (empty for resting columns) so the seeded
            // structure is explicit on disk even for resting columns.
            var instrPath = Path.Combine(columnDir, BoardService.InstructionsFileName);
            await File.WriteAllTextAsync(instrPath, instructions ?? "", ct).ConfigureAwait(false);
        }
    }
}
