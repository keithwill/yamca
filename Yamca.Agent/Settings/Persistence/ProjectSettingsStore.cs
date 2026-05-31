using Yamca.Agent.Workspace;

namespace Yamca.Agent.Settings.Persistence;

/// <summary>Reads and writes the project-tier settings blob at
/// <c>&lt;RepositoryRoot&gt;/.yamca/project.json</c>. Anchors on the supplied workspace's
/// <see cref="IWorkspace.RepositoryRoot"/> (the main repo) so settings travel with the
/// checkout rather than living in per-browser localStorage.
///
/// Deliberately dumb: the store shuttles an opaque JSON string to and from disk and never
/// inspects its shape — <c>SessionSettings.SerializeProject()</c>/<c>HydrateProject()</c>
/// remain the single source of truth for the project blob contract. The project tier holds
/// no secrets (API keys live in the global tier, which stays in localStorage).
///
/// All operations are no-ops outside a git repository (see <see cref="IsEnabled"/>) so we
/// never scatter local state into a non-repo workspace. Reads/writes are serialized through
/// a lock — fine for the single-user, per-circuit usage here.</summary>
public sealed class ProjectSettingsStore
{
    private readonly IWorkspace _workspace;
    private readonly object _gate = new();

    public ProjectSettingsStore(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    private string YamcaDir => Path.Combine(_workspace.RepositoryRoot, ".yamca");
    private string SettingsPath => Path.Combine(YamcaDir, "project.json");

    /// <summary>True only when yamca's managed <c>.yamca/.gitignore</c> is present — i.e.
    /// we are inside a git repository where <see cref="WorkspaceScaffold.EnsureGitignore"/>
    /// has set up ignore rules. Outside a repo every method is a no-op.</summary>
    public bool IsEnabled => File.Exists(Path.Combine(YamcaDir, ".gitignore"));

    /// <summary>Return the raw project-settings blob, or null when missing, unreadable, or
    /// the store is disabled. The caller feeds this straight to
    /// <c>SessionSettings.HydrateProject(json)</c>.</summary>
    public string? Load()
    {
        if (!IsEnabled) return null;
        lock (_gate)
        {
            try
            {
                return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Persist the project-settings blob, overwriting any existing file. No-op when
    /// the store is disabled (not inside a git repository).</summary>
    public void Save(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!IsEnabled) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(YamcaDir);
                WriteAtomic(SettingsPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort: a transient write failure shouldn't crash the circuit
            }
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
