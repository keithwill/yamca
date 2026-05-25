using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Lists directory contents under the workspace root for the Settings
/// file picker. All returned paths are forward-slash relative paths from the root,
/// and traversal outside the workspace is rejected via <see cref="IWorkspace.Resolve"/>.</summary>
public sealed class WorkspaceBrowser
{
    private readonly IWorkspace _workspace;

    public WorkspaceBrowser(IWorkspace workspace)
    {
        _workspace = workspace;
    }

    public record Entry(string Name, string RelativePath, bool IsDirectory);

    public record Listing(string RelativeDir, IReadOnlyList<Entry> Entries);

    public Listing List(string? relativeDir)
    {
        var normalized = string.IsNullOrWhiteSpace(relativeDir) ? string.Empty : relativeDir.Trim().Replace('\\', '/').Trim('/');

        string absoluteDir;
        if (normalized.Length == 0)
        {
            absoluteDir = _workspace.RootPath;
        }
        else
        {
            try
            {
                absoluteDir = _workspace.Resolve(normalized);
            }
            catch (PathOutsideWorkspaceException)
            {
                absoluteDir = _workspace.RootPath;
                normalized = string.Empty;
            }
        }

        if (!Directory.Exists(absoluteDir))
        {
            absoluteDir = _workspace.RootPath;
            normalized = string.Empty;
        }

        var entries = new List<Entry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(absoluteDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                entries.Add(new Entry(name, Join(normalized, name), IsDirectory: true));
            }
            foreach (var file in Directory.EnumerateFiles(absoluteDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                entries.Add(new Entry(name, Join(normalized, name), IsDirectory: false));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return new Listing(normalized, entries);
    }

    public static string? Parent(string relativeDir)
    {
        if (string.IsNullOrWhiteSpace(relativeDir)) return null;
        var idx = relativeDir.LastIndexOf('/');
        return idx < 0 ? string.Empty : relativeDir[..idx];
    }

    private static string Join(string dir, string name) =>
        dir.Length == 0 ? name : $"{dir}/{name}";
}
