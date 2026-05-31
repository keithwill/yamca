namespace Yamca.Agent.Workspace;

/// <summary>Bootstraps the repo-anchored <c>.yamca</c> directory's git hygiene, independent of the
/// board and worktree machinery that also write there.
///
/// yamca stores local-only state under <c>&lt;RepositoryRoot&gt;/.yamca</c> — notably chat history,
/// which a user can create without ever touching a card, branch, or worktree. That state is detected
/// by git but should never be committed, and we don't want users to hand-edit their repository's root
/// <c>.gitignore</c> to silence it. So yamca writes its own <c>.yamca/.gitignore</c> that ignores the
/// local state <em>and itself</em>: git honors an untracked ignore file, so the working tree stays
/// clean with nothing to commit.</summary>
public static class WorkspaceScaffold
{
    // Ignore rules (relative to .yamca/) that the managed .gitignore must always contain. Anchored
    // with a leading slash so they match only at the .yamca root. "/.gitignore" makes the file
    // self-ignoring, keeping `git status` clean without the user committing anything.
    private static readonly string[] RequiredRules = ["/chat/", "/project.json", "/.gitignore"];

    private const string Header =
        "# Managed by yamca — local-only state, not shared. Safe to delete; yamca recreates it.";

    /// <summary>Ensure <c>&lt;repositoryRoot&gt;/.yamca/.gitignore</c> exists and contains the rules
    /// that keep yamca's local-only state (and the ignore file itself) out of git. Idempotent:
    /// creates the file when absent, otherwise appends only the missing rules and preserves any
    /// existing content. Call once at startup, only when inside a git repository.</summary>
    public static void EnsureGitignore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var yamcaDir = Path.Combine(repositoryRoot, ".yamca");
        Directory.CreateDirectory(yamcaDir);

        var gitignorePath = Path.Combine(yamcaDir, ".gitignore");

        if (!File.Exists(gitignorePath))
        {
            var contents = string.Join(Environment.NewLine,
                [Header, .. RequiredRules]) + Environment.NewLine;
            File.WriteAllText(gitignorePath, contents);
            return;
        }

        // File exists: append only the rules that aren't already present, leaving everything else
        // untouched so the file can carry extra entries we don't manage.
        var existingLines = File.ReadAllLines(gitignorePath);
        var present = new HashSet<string>(existingLines.Select(l => l.Trim()), StringComparer.Ordinal);

        var missing = RequiredRules.Where(rule => !present.Contains(rule)).ToArray();
        if (missing.Length == 0)
            return;

        var addition = string.Join(Environment.NewLine, missing) + Environment.NewLine;

        // Start the appended block on its own line if the file doesn't already end with a newline.
        var existingText = File.ReadAllText(gitignorePath);
        var prefix = existingText.Length > 0 && !existingText.EndsWith('\n')
            ? Environment.NewLine
            : "";

        File.AppendAllText(gitignorePath, prefix + addition);
    }
}
