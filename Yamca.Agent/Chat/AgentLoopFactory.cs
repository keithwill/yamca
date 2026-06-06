using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Builds <see cref="AgentLoop"/> instances, capturing the stable per-circuit
/// collaborators (tool registry, permission and availability resolvers, approval
/// coordinator, permission store, loaded tool set) so a caller only supplies the
/// per-session pieces: the session, its completion client and workspace, and run-time
/// options. Lets a per-circuit orchestrator such as <c>ChatViewModel</c> construct a loop
/// without taking a direct constructor dependency on every collaborator that exists only
/// to be threaded into the loop.</summary>
public sealed class AgentLoopFactory
{
    private readonly IToolRegistry _tools;
    private readonly IPermissionResolver _permissions;
    private readonly IAvailabilityResolver _availability;
    private readonly IApprovalCoordinator _approvals;
    private readonly IPermissionStore _permissionStore;
    private readonly LoadedToolSet _loadedTools;

    public AgentLoopFactory(
        IToolRegistry tools,
        IPermissionResolver permissions,
        IAvailabilityResolver availability,
        IApprovalCoordinator approvals,
        IPermissionStore permissionStore,
        LoadedToolSet loadedTools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(permissionStore);
        ArgumentNullException.ThrowIfNull(loadedTools);

        _tools = tools;
        _permissions = permissions;
        _availability = availability;
        _approvals = approvals;
        _permissionStore = permissionStore;
        _loadedTools = loadedTools;
    }

    /// <summary>Construct an <see cref="AgentLoop"/> for one session. <paramref name="workspace"/>
    /// is supplied per call rather than captured because a worktree-bound session overrides the
    /// DI-resolved root workspace.</summary>
    public AgentLoop Create(
        ChatSession session,
        IChatCompletionClient client,
        IWorkspace workspace,
        AgentLoopOptions? options = null,
        Func<bool>? isYoloEnabled = null,
        SessionDiagnosticsLog? diagnostics = null)
        => new(
            session, client, _tools, _permissions, _availability, _approvals,
            _permissionStore, workspace, _loadedTools, options, isYoloEnabled, diagnostics);
}
