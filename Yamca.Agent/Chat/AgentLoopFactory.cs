using Yamca.Agent.Metrics;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Builds <see cref="AgentLoop"/> instances, capturing the stable per-circuit
/// collaborators (tool registry, permission and availability resolvers, permission store) so a
/// caller only supplies the per-session pieces: the session, its completion client and workspace,
/// the session-owned approval coordinator and loaded-tool set, and run-time options. Lets a
/// per-circuit orchestrator such as <c>ChatViewModel</c> construct a loop without taking a direct
/// constructor dependency on every collaborator that exists only to be threaded into the loop.
///
/// The approval coordinator and loaded-tool set are deliberately <em>not</em> captured here: a
/// circuit hosts several chat panes, but each needs its own approval queue and loaded-tool set, so
/// these are owned per session by <c>ChatViewModel</c> and passed into <see cref="Create"/>.</summary>
public sealed class AgentLoopFactory
{
    private readonly IToolRegistry _tools;
    private readonly IPermissionResolver _permissions;
    private readonly IAvailabilityResolver _availability;
    private readonly IPermissionStore _permissionStore;
    private readonly ITurnMetricSink? _metrics;

    public AgentLoopFactory(
        IToolRegistry tools,
        IPermissionResolver permissions,
        IAvailabilityResolver availability,
        IPermissionStore permissionStore,
        ITurnMetricSink? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(permissionStore);

        _tools = tools;
        _permissions = permissions;
        _availability = availability;
        _permissionStore = permissionStore;
        _metrics = metrics;
    }

    /// <summary>Construct an <see cref="AgentLoop"/> for one session. <paramref name="workspace"/>,
    /// <paramref name="approvals"/> and <paramref name="loadedTools"/> are supplied per call rather
    /// than captured: the workspace because a worktree-bound session overrides the DI-resolved root
    /// workspace, and the latter two because they are owned per session (see the type remarks).</summary>
    public AgentLoop Create(
        ChatSession session,
        IChatCompletionClient client,
        IWorkspace workspace,
        IApprovalCoordinator approvals,
        LoadedToolSet loadedTools,
        AgentLoopOptions? options = null,
        Func<bool>? isYoloEnabled = null,
        SessionDiagnosticsLog? diagnostics = null)
        => new(
            session, client, _tools, _permissions, _availability, approvals,
            _permissionStore, workspace, loadedTools, options, isYoloEnabled, diagnostics, _metrics);
}
