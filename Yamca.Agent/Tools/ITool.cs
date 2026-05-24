using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema describing this tool's arguments, as a UTF-8 JSON string.</summary>
    string ParametersSchema { get; }

    /// <summary>
    /// True if this tool reads/writes the filesystem and can therefore be
    /// confined to the workspace root. Surfaces a toggle in the settings UI.
    /// </summary>
    bool SupportsWorkspaceRestriction { get; }

    /// <summary>Default permission when neither project nor global settings override it.</summary>
    PermissionLevel DefaultPermission { get; }

    Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken);
}
