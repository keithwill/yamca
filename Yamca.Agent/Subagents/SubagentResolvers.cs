using System.Text.Json;
using System.Threading.Channels;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Subagents;

/// <summary>Permission resolver for a headless subagent loop: every tool is auto-allowed
/// (the subagent runs without a UI to prompt). Workspace restriction follows the subagent's
/// single <c>RestrictToWorkspace</c> setting, applied only to tools that actually support it.</summary>
internal sealed class SubagentPermissionResolver : IPermissionResolver
{
    private readonly IToolRegistry _tools;
    private readonly bool _restrictToWorkspace;

    public SubagentPermissionResolver(IToolRegistry tools, bool restrictToWorkspace)
    {
        _tools = tools;
        _restrictToWorkspace = restrictToWorkspace;
    }

    public PermissionLevel Resolve(string toolName) => PermissionLevel.Allow;

    public bool RestrictToWorkspace(string toolName) =>
        _restrictToWorkspace && (_tools.Get(toolName)?.SupportsWorkspaceRestriction ?? false);
}

/// <summary>Availability resolver for a subagent loop: every tool in the subagent's private
/// registry is Eager. The subagent's tool set is already curated, so there is no need for the
/// deferred/lookup machinery — the model sees exactly its allowed tools up front.</summary>
internal sealed class SubagentAvailabilityResolver : IAvailabilityResolver
{
    public Availability Resolve(string toolName) => Availability.Eager;
}

/// <summary>No-op approval coordinator for a subagent loop. Never exercised because the
/// subagent's permission resolver returns <see cref="PermissionLevel.Allow"/> for everything;
/// supplied only to satisfy the <see cref="Chat.AgentLoop"/> constructor.</summary>
internal sealed class NoopApprovalCoordinator : IApprovalCoordinator
{
    public ChannelReader<ApprovalRequest> Pending { get; } =
        Channel.CreateUnbounded<ApprovalRequest>().Reader;

    public Task<ApprovalDecision> RequestApprovalAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalDecision(true, ApprovalPersistence.None));
}

/// <summary>No-op permission store for a subagent loop — subagent decisions are never
/// persisted. Supplied only to satisfy the <see cref="Chat.AgentLoop"/> constructor.</summary>
internal sealed class NoopPermissionStore : IPermissionStore
{
    public void Persist(string toolName, PermissionLevel decision, ApprovalPersistence tier) { }
}
