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

    /// <summary>Default permission when neither project nor user settings override it.</summary>
    PermissionLevel DefaultPermission { get; }

    /// <summary>False = the permission level is fixed at <see cref="DefaultPermission"/> and not
    /// user-configurable (e.g. <c>execute_allowed</c>, which exists precisely to be the always-Allow
    /// path for curated entry points). The settings UI renders a static chip instead of a dropdown
    /// and the User tier never materializes a permission override for it. Default: true.</summary>
    bool ConfigurablePermission => true;

    /// <summary>True (default) = include in the tool list sent to the LLM.</summary>
    bool ExposedToLlm => true;

    /// <summary>True = excluded from the initial tool list; the LLM discovers this tool's schema
    /// via <c>lookup_tool</c> and invokes it through <c>call_tool</c>. The schema is never added to
    /// the prompt prefix, so loading it mid-session does not invalidate the prefix cache.
    /// Default: false. Source of truth for <see cref="DefaultAvailability"/>; user settings override.</summary>
    bool Deferred => false;

    /// <summary>Default availability when the user has not set a per-tool override.
    /// Derived from <see cref="Deferred"/> by default so existing tools keep their behavior.</summary>
    Availability DefaultAvailability => Deferred ? Availability.Deferred : Availability.Eager;

    /// <summary>True = tool cannot be Deferred or Hidden by the user (e.g. <c>lookup_tool</c> /
    /// <c>call_tool</c>; without them the LLM cannot discover or invoke deferred tools at all).
    /// UI locks the dropdown.</summary>
    bool MandatoryEager => false;

    /// <summary>False = the Hidden option is suppressed in the UI. Use for tools the harness
    /// invokes outside the model's tool-call loop (currently none) or that would brick the
    /// LLM if hidden. Default: true.</summary>
    bool CanBeHidden => true;

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
