namespace Yamca.Agent.Permissions;

public interface IPermissionResolver
{
    /// <summary>
    /// Resolve the effective permission for a tool by merging project → global → tool default.
    /// </summary>
    PermissionLevel Resolve(string toolName);

    /// <summary>
    /// Resolve whether the workspace-restriction sandbox is on for this tool.
    /// Same precedence as <see cref="Resolve"/>; defaults to true for tools that
    /// declare <c>SupportsWorkspaceRestriction</c>, false otherwise.
    /// </summary>
    bool RestrictToWorkspace(string toolName);
}
