using System.Diagnostics;
using System.Text;

namespace Yamca.Agent.Git;

public enum MergeStrategy
{
    Merge,
    Rebase,
    Squash,
}

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

public sealed record WorktreeInfo(
    string Branch,
    string BaseBranch,
    string WorktreePath,
    string BasePath);

/// <summary>One entry from <c>git log</c> for a file: commit hash, author date, subject.</summary>
public sealed record GitFileLogEntry(string Sha, DateTimeOffset Date, string Subject);

/// <summary>Thin wrapper around the <c>git</c> CLI. All methods return a
/// <see cref="GitResult"/> rather than throwing on non-zero exit so callers can
/// surface stderr to the user.</summary>
public sealed class GitService
{
    public async Task<bool> IsGitRepoAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(path, ["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
        return r.Ok && r.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Absolute path to the top-level working directory of the repository containing
    /// <paramref name="path"/> (<c>git rev-parse --show-toplevel</c>), or null when
    /// <paramref name="path"/> is not inside a git work tree. For a linked worktree this returns
    /// that worktree's own root, not the main repository's.</summary>
    public async Task<string?> GetRepoRootAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(path, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var top = r.Stdout.Trim();
        return string.IsNullOrEmpty(top) ? null : top;
    }

    public async Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(path, ["symbolic-ref", "--short", "HEAD"], ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var name = r.Stdout.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(path, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], ct).ConfigureAwait(false);
        if (!r.Ok) return Array.Empty<string>();
        return r.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>Returns existing worktrees of the repo as (path, branch) pairs.
    /// Branch is null for detached HEAD or the bare/main entry without a checked-out branch.</summary>
    public async Task<IReadOnlyList<(string Path, string? Branch)>> ListWorktreesAsync(string repoPath, CancellationToken ct)
    {
        var r = await RunAsync(repoPath, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        if (!r.Ok) return Array.Empty<(string, string?)>();

        var results = new List<(string, string?)>();
        string? path = null;
        string? branch = null;

        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path is not null) results.Add((path, branch));
                path = null;
                branch = null;
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                path = line["worktree ".Length..].Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line["branch ".Length..].Trim();
                const string prefix = "refs/heads/";
                branch = refName.StartsWith(prefix, StringComparison.Ordinal) ? refName[prefix.Length..] : refName;
            }
        }
        if (path is not null) results.Add((path, branch));
        return results;
    }

    public Task<GitResult> CreateWorktreeAsync(string repoPath, string worktreePath, string branch, bool isNewBranch, CancellationToken ct)
    {
        var args = isNewBranch
            ? new[] { "worktree", "add", worktreePath, "-b", branch }
            : new[] { "worktree", "add", worktreePath, branch };
        return RunAsync(repoPath, args, ct);
    }

    public Task<GitResult> MergeAsync(string repoPath, string branch, MergeStrategy strategy, CancellationToken ct)
    {
        return strategy switch
        {
            MergeStrategy.Merge => RunAsync(repoPath, ["merge", "--no-ff", branch], ct),
            MergeStrategy.Rebase => RunAsync(repoPath, ["rebase", branch], ct),
            MergeStrategy.Squash => SquashAsync(repoPath, branch, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };
    }

    private async Task<GitResult> SquashAsync(string repoPath, string branch, CancellationToken ct)
    {
        var squash = await RunAsync(repoPath, ["merge", "--squash", branch], ct).ConfigureAwait(false);
        if (!squash.Ok) return squash;

        var gitDir = await RunAsync(repoPath, ["rev-parse", "--git-dir"], ct).ConfigureAwait(false);
        if (!gitDir.Ok) return gitDir;
        var squashMsgPath = Path.Combine(repoPath, gitDir.Stdout.Trim(), "SQUASH_MSG");

        return File.Exists(squashMsgPath)
            ? await RunAsync(repoPath, ["commit", "-F", squashMsgPath], ct).ConfigureAwait(false)
            : await RunAsync(repoPath, ["commit", "-m", $"Squash merge of {branch}"], ct).ConfigureAwait(false);
    }

    public Task<GitResult> RemoveWorktreeAsync(string repoPath, string worktreePath, bool force, CancellationToken ct)
    {
        var args = force
            ? new[] { "worktree", "remove", "--force", worktreePath }
            : new[] { "worktree", "remove", worktreePath };
        return RunAsync(repoPath, args, ct);
    }

    public Task<GitResult> DeleteBranchAsync(string repoPath, string branch, bool force, CancellationToken ct)
    {
        var flag = force ? "-D" : "-d";
        return RunAsync(repoPath, ["branch", flag, branch], ct);
    }

    public async Task<int> CountCommitsAheadAsync(string repoPath, string baseBranch, string branch, CancellationToken ct)
    {
        var r = await RunAsync(repoPath, ["rev-list", "--count", $"{baseBranch}..{branch}"], ct).ConfigureAwait(false);
        if (!r.Ok) return 0;
        return int.TryParse(r.Stdout.Trim(), out var n) ? n : 0;
    }

    public async Task<bool> CheckRefFormatAsync(string branch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch)) return false;
        var r = await RunAsync(null, ["check-ref-format", "--branch", branch], ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Stage a rename/move (<c>git mv</c>). Leaves the move staged but uncommitted so
    /// the caller (typically an LLM completing a step) can bundle it into a later commit.</summary>
    public Task<GitResult> MoveAsync(string repoPath, string from, string to, CancellationToken ct)
        => RunAsync(repoPath, ["mv", from, to], ct);

    /// <summary>Outcome of <see cref="MoveWithUntrackedFallbackAsync"/>. <see cref="Ok"/> means the
    /// file now lives at the destination; <see cref="Staged"/> says whether the relocation was
    /// recorded in the index (false only when the fallback <c>git add</c> failed, e.g. outside a
    /// repo); <see cref="CommitPaths"/> are the repo-relative paths to hand to
    /// <see cref="CommitStagedPathsAsync"/> — both sides for a tracked rename, the destination only
    /// for an untracked move.</summary>
    public sealed record StagedMove(bool Ok, bool Staged, IReadOnlyList<string> CommitPaths, string Error);

    /// <summary>Move a file with <c>git mv</c>, falling back to a filesystem move plus an explicit
    /// stage of the destination when <c>git mv</c> fails (a never-committed/untracked file). Either
    /// way the relocation is staged but NOT committed, so the caller chooses whether to bundle it
    /// into a larger commit (the agent's step commit) or commit it in isolation via
    /// <see cref="CommitStagedPathsAsync"/> (the board UI). Consolidates the move-with-fallback dance
    /// shared by the board move tool and the UI's promote/move actions.</summary>
    public async Task<StagedMove> MoveWithUntrackedFallbackAsync(string repoRoot, string srcAbs, string destAbs, CancellationToken ct)
    {
        var relSrc = Path.GetRelativePath(repoRoot, srcAbs);
        var relDest = Path.GetRelativePath(repoRoot, destAbs);

        // A tracked card moves with git mv, which stages both sides of the rename.
        var mv = await MoveAsync(repoRoot, srcAbs, destAbs, ct).ConfigureAwait(false);
        if (mv.Ok)
            return new StagedMove(Ok: true, Staged: true, new[] { relSrc, relDest }, "");

        // git mv fails for a never-committed (untracked) file. Fall back to a filesystem move, then
        // best-effort stage the new location so the relocation still rides along with a later commit.
        try
        {
            File.Move(srcAbs, destAbs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StagedMove(Ok: false, Staged: false, Array.Empty<string>(),
                $"git mv failed ({mv.Stderr.Trim()}) and the fallback move failed: {ex.Message}");
        }

        // The file is moved regardless; staging is best-effort (it fails outside a git repo), so a
        // failed add still reports Ok with Staged=false rather than masking a completed move.
        var add = await AddAsync(repoRoot, relDest, ct).ConfigureAwait(false);
        return new StagedMove(Ok: true, Staged: add.Ok, new[] { relDest }, "");
    }

    /// <summary>Stage a path (<c>git add</c>). Used as a fallback when moving a not-yet-tracked
    /// card file, so the relocation still rides along with the next commit.</summary>
    public Task<GitResult> AddAsync(string repoPath, string pathspec, CancellationToken ct)
        => RunAsync(repoPath, ["add", "--", pathspec], ct);

    /// <summary>True when <paramref name="pathspec"/> has uncommitted changes (staged, unstaged,
    /// or untracked) relative to HEAD. Lets a caller skip an isolated commit when there is nothing
    /// to commit, avoiding a spurious "nothing to commit" failure.</summary>
    public async Task<bool> HasUncommittedChangesAsync(string repoPath, string pathspec, CancellationToken ct)
    {
        var r = await RunAsync(repoPath, ["status", "--porcelain", "--", pathspec], ct).ConfigureAwait(false);
        return r.Ok && !string.IsNullOrWhiteSpace(r.Stdout);
    }

    /// <summary>Stage and commit only <paramref name="pathspecs"/> in a single commit, leaving any
    /// other staged or unstaged changes in the working tree untouched. The pathspec-scoped
    /// <c>git commit -- …</c> performs a partial commit that ignores the rest of the index. Used to
    /// commit a board card (with its branch binding) to the current branch before forking a worktree,
    /// so the card is visible on the resulting branch without sweeping in unrelated in-flight work.</summary>
    public async Task<GitResult> CommitPathsAsync(string repoPath, string message, IReadOnlyList<string> pathspecs, CancellationToken ct)
    {
        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(pathspecs);
        var add = await RunAsync(repoPath, addArgs, ct).ConfigureAwait(false);
        if (!add.Ok) return add;

        return await CommitStagedPathsAsync(repoPath, message, pathspecs, ct).ConfigureAwait(false);
    }

    /// <summary>Commit already-staged changes for <paramref name="pathspecs"/> only, staging nothing
    /// first. Like <see cref="CommitPathsAsync"/> this is a pathspec-scoped partial commit that
    /// leaves the rest of the index untouched, but it must be used when the changes are already
    /// staged and a fresh <c>git add</c> would fail — notably a <c>git mv</c> rename, whose source
    /// path is gone from the index, so re-adding it fatals with "pathspec did not match".</summary>
    public Task<GitResult> CommitStagedPathsAsync(string repoPath, string message, IReadOnlyList<string> pathspecs, CancellationToken ct)
    {
        var commitArgs = new List<string> { "commit", "-m", message, "--" };
        commitArgs.AddRange(pathspecs);
        return RunAsync(repoPath, commitArgs, ct);
    }

    /// <summary>Author date of the commit that first added <paramref name="filePath"/>
    /// (following renames), or null when the file has never been committed.</summary>
    public async Task<DateTimeOffset?> GetFileCreatedAtAsync(string repoPath, string filePath, CancellationToken ct)
    {
        var r = await RunAsync(repoPath, ["log", "--follow", "--diff-filter=A", "--format=%aI", "--", filePath], ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        // Multiple add commits are possible (delete + re-add); the oldest is the last line.
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        return DateTimeOffset.TryParse(lines[^1], out var dt) ? dt : null;
    }

    /// <summary>Author date of the most recent commit touching <paramref name="filePath"/>
    /// (following renames), or null when uncommitted.</summary>
    public async Task<DateTimeOffset?> GetFileLastModifiedAtAsync(string repoPath, string filePath, CancellationToken ct)
    {
        var r = await RunAsync(repoPath, ["log", "--follow", "-1", "--format=%aI", "--", filePath], ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var line = r.Stdout.Trim();
        return DateTimeOffset.TryParse(line, out var dt) ? dt : null;
    }

    /// <summary>Commit history for a file (newest first, following renames).</summary>
    public async Task<IReadOnlyList<GitFileLogEntry>> GetFileHistoryAsync(string repoPath, string filePath, CancellationToken ct)
    {
        const char sep = '\x1f';
        var r = await RunAsync(repoPath, ["log", "--follow", $"--format=%H{sep}%aI{sep}%s", "--", filePath], ct).ConfigureAwait(false);
        if (!r.Ok) return Array.Empty<GitFileLogEntry>();

        var entries = new List<GitFileLogEntry>();
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split(sep);
            if (parts.Length < 3) continue;
            if (!DateTimeOffset.TryParse(parts[1], out var dt)) continue;
            entries.Add(new GitFileLogEntry(parts[0], dt, parts[2]));
        }
        return entries;
    }

    private static async Task<GitResult> RunAsync(string? workingDir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new GitResult(-1, "", $"failed to launch git: {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        return new GitResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
