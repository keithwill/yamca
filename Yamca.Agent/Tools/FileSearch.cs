using Microsoft.Extensions.FileSystemGlobbing;

namespace Yamca.Agent.Tools;

/// <summary>
/// Shared filesystem walking for <see cref="FindFilesTool"/> and <see cref="GrepTool"/>.
/// Walks a directory tree honoring a glob filter, a built-in always-skip directory
/// list, and (optionally) the root <c>.gitignore</c>.
/// </summary>
internal static class FileSearch
{
    /// <summary>Directories pruned regardless of <c>respect_gitignore</c>.</summary>
    private static readonly string[] AlwaysSkipDirs =
    {
        ".git", "node_modules", "bin", "obj", ".vs", "dist", "out", ".idea",
    };

    private static readonly StringComparer DirNameComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> AlwaysSkipSet = new(AlwaysSkipDirs, DirNameComparer);

    /// <summary>
    /// Enumerate files under <paramref name="root"/> whose path matches <paramref name="includeGlob"/>.
    /// Paths are absolute. The enumeration is depth-first and prunes always-skip dirs
    /// (and, when <paramref name="respectGitignore"/> is true, anything matched by the
    /// root <c>.gitignore</c>).
    /// </summary>
    public static IEnumerable<string> Enumerate(
        string root,
        string includeGlob,
        bool respectGitignore,
        CancellationToken cancellationToken)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(includeGlob);

        var ignore = respectGitignore ? LoadRootGitignore(root) : null;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = stack.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry);
                if (AlwaysSkipSet.Contains(name)) continue;

                bool isDir;
                try { isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0; }
                catch { continue; }

                var relForward = ToForwardSlashRelative(root, entry);

                if (isDir)
                {
                    // gitignore directory rules conventionally test with a trailing slash.
                    if (ignore is not null && ignore.IsIgnored(relForward + "/")) continue;
                    stack.Push(entry);
                }
                else
                {
                    if (ignore is not null && ignore.IsIgnored(relForward)) continue;
                    if (!matcher.Match(relForward).HasMatches) continue;
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Convert an absolute path under <paramref name="root"/> to a forward-slash relative path.</summary>
    public static string ToForwardSlashRelative(string root, string absolute)
    {
        var rel = Path.GetRelativePath(root, absolute);
        return rel.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static Ignore.Ignore? LoadRootGitignore(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path)) return null;

        try
        {
            var lines = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
            var ig = new Ignore.Ignore();
            ig.Add(lines);
            return ig;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
