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

    /// <summary>The dispatcher's call id for this tool invocation, when one exists.
    /// Lets a tool correlate itself back to the UI card that represents the call
    /// (e.g. <c>subagent_run</c> keys its live session on this so the parent chat
    /// can open the matching transcript). Null when invoked outside the agent loop
    /// (e.g. <see cref="ITool.SessionStartMessage"/> probes).</summary>
    public string? CallId { get; }

    /// <summary>Identifies the chat session whose loop is invoking the tool, when known.
    /// Lets a tool attribute side effects back to the originating session (e.g. a
    /// subagent run is shown only in the chat that launched it). Flows from the parent
    /// loop's options, so it is correct even when multiple sessions run concurrently.</summary>
    public string? OwnerId { get; }

    public ToolContext(IWorkspace workspace, bool restrictToWorkspace, string? callId = null, string? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        RestrictToWorkspace = restrictToWorkspace;
        CallId = callId;
        OwnerId = ownerId;
    }
}
