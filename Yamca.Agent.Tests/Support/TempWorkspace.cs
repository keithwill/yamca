using Yamca.Agent.Workspace;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Support;

/// <summary>
/// Creates a fresh temp directory and binds a Workspace to it. Disposes the directory.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public string RootPath { get; }
    public IWorkspace Workspace { get; }

    public TempWorkspace()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        RootPath = Path.Combine(baseDir, "yamca-tests", "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        Workspace = new WorkspaceImpl(RootPath);
    }

    public string WriteFile(string relative, string content)
    {
        var full = Path.Combine(RootPath, relative);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true); }
        catch { /* best-effort */ }
    }
}
