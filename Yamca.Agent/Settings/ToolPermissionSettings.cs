using Yamca.Agent.Permissions;

namespace Yamca.Agent.Settings;

/// <summary>
/// Per-tool override values stored in either the project or global settings tier.
/// Nullable fields denote "not set — fall through to the next tier".
/// </summary>
public sealed record ToolPermissionSettings
{
    public PermissionLevel? Permission { get; init; }
    public bool? RestrictToWorkspace { get; init; }
}
