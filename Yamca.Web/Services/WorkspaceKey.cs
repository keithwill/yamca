using System.Security.Cryptography;
using System.Text;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Stable per-workspace localStorage key. SHA-256 of the canonical root path,
/// lowercased on case-insensitive filesystems so the same workspace from different
/// casings still resolves to one key.</summary>
public sealed class WorkspaceKey
{
    public string GlobalKey => "yamca.global";
    public string ProjectKey { get; }

    public WorkspaceKey(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var normalized = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? workspace.RootPath.ToLowerInvariant()
            : workspace.RootPath;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexStringLower(hash);
        ProjectKey = $"yamca.project.{hex}";
    }
}
