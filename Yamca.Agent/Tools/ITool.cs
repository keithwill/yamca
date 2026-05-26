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

    /// <summary>True (default) = include in the tool list sent to the LLM.</summary>
    bool ExposedToLlm => true;

    /// <summary>True (default) = show in the settings permissions table.</summary>
    bool ExposedInSettings => true;

    Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Optional contribution to the session-start system message. Returning a non-null
    /// string appends it (separated by a blank line) to the single system message the
    /// chat builder constructs. Use this when a tool needs to expose runtime state to
    /// the LLM that cannot fit in its static <see cref="Description"/> — e.g., a list
    /// of registered scripts. Default: no contribution.
    /// </summary>
    string? SessionStartMessage(ToolContext context) => null;
}
