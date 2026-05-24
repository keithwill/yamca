using Yamca.Agent.Workspace;

namespace Yamca.Agent.Tools;

/// <summary>
/// Runtime context handed to a tool for a single invocation.
/// <see cref="RestrictToWorkspace"/> is resolved per-call from session settings so
/// the user can toggle it without rebuilding the registry.
/// </summary>
public sealed class ToolContext
{
    public IWorkspace Workspace { get; }
    public bool RestrictToWorkspace { get; }

    public ToolContext(IWorkspace workspace, bool restrictToWorkspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        RestrictToWorkspace = restrictToWorkspace;
    }
}
