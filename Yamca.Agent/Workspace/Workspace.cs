namespace Yamca.Agent.Workspace;

public sealed class Workspace : IWorkspace
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public string RootPath { get; }

    public Workspace(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var canonical = ResolveSymlinks(Path.GetFullPath(rootPath));
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"Workspace root '{canonical}' does not exist.");

        RootPath = TrimTrailingSeparator(canonical);
    }

    public string Resolve(string requestedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestedPath);

        var combined = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.Combine(RootPath, requestedPath);

        var canonical = Path.GetFullPath(combined);
        var resolved = ResolveSymlinks(canonical);

        if (!IsWithinRoot(resolved))
            throw new PathOutsideWorkspaceException(requestedPath, resolved, RootPath);

        return resolved;
    }

    private bool IsWithinRoot(string fullPath)
    {
        if (fullPath.Equals(RootPath, PathComparison))
            return true;

        var rootWithSep = RootPath + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSep, PathComparison);
    }

    // Walks each existing segment of the path and resolves symlinks/junctions to their
    // final target. Non-existing tail segments (e.g. a file we're about to create) are
    // appended verbatim. This catches escapes via symlinks at any depth, not just the leaf.
    private static string ResolveSymlinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return fullPath;

        var remainder = fullPath[root.Length..];
        var parts = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            FileSystemInfo? info = null;
            if (Directory.Exists(current)) info = new DirectoryInfo(current);
            else if (File.Exists(current)) info = new FileInfo(current);

            if (info is null)
                continue;

            try
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    current = target.FullName;
            }
            catch (IOException)
            {
                // Broken or cyclic link — leave the segment as-is; downstream
                // file operations will surface a more specific error.
            }
        }

        return Path.GetFullPath(current);
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1) return path;

        // Don't strip the separator off a drive root like "C:\" or a Unix root "/".
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && path.Equals(root, PathComparison))
            return path;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
