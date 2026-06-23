using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
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

    /// <summary>The originating session's approval coordinator. Tools that run their own internal
    /// permission gate (e.g. <c>git</c>, <c>start_process</c>) raise approval prompts through this
    /// rather than resolving one from the DI scope — the scope is per browser circuit, but a circuit
    /// hosts several chat panes, so a scope-resolved coordinator would surface the prompt in an
    /// arbitrary pane. The agent loop owns one coordinator per session and stamps it here, so the
    /// prompt always lands in the session that invoked the tool. Null only outside the loop (e.g.
    /// <see cref="ITool.SessionStartMessage"/> probes), where no tool reaches the approval path.</summary>
    public IApprovalCoordinator? Approvals { get; }

    /// <summary>The originating session's set of deferred tools the model has already loaded. Owned
    /// per session by the agent loop (same reasoning as <see cref="Approvals"/>: circuit scope is too
    /// coarse) and stamped here so <c>lookup_tool</c> marks tools loaded on the same instance the loop
    /// reads for its self-correction check. Null outside the loop.</summary>
    public LoadedToolSet? LoadedTools { get; }

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

    public ToolContext(
        IWorkspace workspace,
        bool restrictToWorkspace,
        string? callId = null,
        string? ownerId = null,
        IApprovalCoordinator? approvals = null,
        LoadedToolSet? loadedTools = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        RestrictToWorkspace = restrictToWorkspace;
        CallId = callId;
        OwnerId = ownerId;
        Approvals = approvals;
        LoadedTools = loadedTools;
    }
}
