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
