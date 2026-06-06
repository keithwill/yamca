using System.Text;
using System.Text.Json;
using Yamca.Agent.Chat;
using Yamca.Agent.Chat.Prompts;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Subagents;

/// <summary>Builds and drives a headless child <see cref="AgentLoop"/> for a configured
/// subagent. The child inherits the parent's workspace (via the calling tool's
/// <see cref="ToolContext"/>) and, by default, the parent's endpoint; it runs a curated,
/// auto-allowed tool set plus a private <c>subagent_result</c> tool. The run ends as soon as
/// the subagent delivers a result — anything else (clarifying questions, parse errors, the
/// iteration cap) yields an error instead of leaking the subagent's confused output.</summary>
public sealed class SubagentRunner : ISubagentRunner
{
    private const int FailureTailChars = 600;

    private readonly ISessionSettings _settings;
    private readonly IServiceProvider _services;
    private readonly IApprovalCoordinator _approvals;
    private readonly EndpointClientFactory _clientFactory;
    private readonly ISubagentObserver _observer;

    private IChatCompletionClient? _parentClient;

    // IToolRegistry is resolved lazily (at RunAsync time), not injected, to avoid a construction
    // cycle: IToolRegistry's factory enumerates every ITool — including SubagentRunTool, which
    // depends on this runner. Resolving it eagerly here would loop IToolRegistry → ITool →
    // SubagentRunner → IToolRegistry. By run time the runner is already built and cached in the
    // scope, so the same resolution returns cleanly.
    public SubagentRunner(
        ISessionSettings settings,
        IServiceProvider services,
        IApprovalCoordinator approvals,
        EndpointClientFactory clientFactory,
        ISubagentObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(clientFactory);
        _settings = settings;
        _services = services;
        _approvals = approvals;
        _clientFactory = clientFactory;
        _observer = observer ?? NoopSubagentObserver.Instance;
    }

    public void Bind(IChatCompletionClient parentClient)
    {
        ArgumentNullException.ThrowIfNull(parentClient);
        _parentClient = parentClient;
    }

    public async Task<ToolResult> RunAsync(
        string agentName,
        string prompt,
        ToolContext parentContext,
        CancellationToken cancellationToken)
    {
        var outcome = await RunCoreAsync(agentName, prompt, parentContext, loopRunId: null, cancellationToken)
            .ConfigureAwait(false);

        // Map the structured outcome to a ToolResult for the single-delegation case. Semantic
        // failure (the subagent said "I couldn't") now surfaces as an error, not silent prose;
        // needs_followup stays a success but is tagged so the parent can spot it.
        if (outcome.IsFailure)
            return ToolResult.Error(outcome.Summary);
        if (outcome.IsNeedsFollowup)
            return ToolResult.Ok("[needs follow-up] " + outcome.Summary);
        return ToolResult.Ok(outcome.Summary);
    }

    public async Task<SubagentOutcome> RunCoreAsync(
        string agentName,
        string prompt,
        ToolContext parentContext,
        string? loopRunId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentContext);

        var def = SubagentRegistry.Resolve(_settings.UserSubagents, _settings.ProjectSubagents, agentName);
        if (def is null)
            return Mechanical($"Unknown subagent '{agentName}'. {AvailableAgentsHint()}");

        if (string.IsNullOrWhiteSpace(prompt))
            return Mechanical("A non-empty 'prompt' is required to run a subagent.");

        // Per-subagent gate: force a parent approval prompt even when subagent_run itself is
        // set to Allow (for the occasional expensive agent). Surfaces in the parent UI via the
        // shared approval coordinator.
        if (def.RequireApproval)
        {
            using var argsDoc = JsonDocument.Parse(
                JsonSerializer.Serialize(new { agent = def.Name, prompt }));
            var decision = await _approvals
                .RequestApprovalAsync("subagent_run", argsDoc.RootElement, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.Approved)
                return Mechanical($"Subagent '{def.Name}' was not approved to run.");
        }

        var client = ResolveClient(def);
        if (client is null)
            return Mechanical(
                $"Subagent '{def.Name}' references an endpoint that no longer exists and no parent endpoint is available.");

        // Build the subagent's private tool set: its allowed tools (sourced from the parent
        // registry snapshot) plus the result tool. subagent_run and loop are deliberately never
        // included, so a subagent cannot spawn further subagents or fan out.
        var registry = (IToolRegistry?)_services.GetService(typeof(IToolRegistry))
            ?? throw new InvalidOperationException("IToolRegistry is not registered.");

        var sink = new SubagentResultSink();
        var allowed = new HashSet<string>(def.AllowedTools, StringComparer.Ordinal);
        var childTools = registry.Tools
            .Where(t => allowed.Contains(t.Name)
                && t.Name != SubagentRunTool.ToolName
                && t.Name != LoopTool.ToolName)
            .ToList<ITool>();
        childTools.Add(new SubagentResultTool(sink));

        var childRegistry = new ToolRegistry(childTools);
        var permissions = new SubagentPermissionResolver(childRegistry, def.RestrictToWorkspace);
        var availability = new SubagentAvailabilityResolver();

        var session = BuildSession(def, parentContext, childTools, permissions);

        var maxIterations = def.MaxIterations is int m and > 0 ? m : _settings.MaxToolIterations;
        var loop = new AgentLoop(
            session,
            client,
            childRegistry,
            permissions,
            availability,
            new NoopApprovalCoordinator(),
            new NoopPermissionStore(),
            parentContext.Workspace,
            new LoadedToolSet(),
            new AgentLoopOptions { MaxIterations = maxIterations },
            isYoloEnabled: static () => true);

        // Mirror the run to any observer (the UI) so it can be watched live. The run id keys
        // the live session; the parent tool-call id (when present) lets the UI open the matching
        // transcript from the subagent_run card; the loop run id (when present) groups this run
        // under its parent loop. OnCompleted always fires (see finally).
        var runId = Guid.NewGuid().ToString("n");
        _observer.OnStarted(new SubagentRunInfo(
            runId, parentContext.CallId, parentContext.OwnerId, def.Name, prompt, DateTimeOffset.Now, loopRunId));

        // Initial context snapshot: at this point the session holds only the system message and the
        // (fixed) tool set, so a running subagent can already show its system prompt and tools. It
        // is refreshed with the full message log on completion below.
        PublishContext(runId, loop);

        var outcome = Mechanical(FailureMessage(def.Name, null, ""));
        try
        {
            var lastAssistant = "";
            TurnCompletionReason? reason = null;

            // Drive one turn: forward its events to the observer, capture the last assistant text
            // and completion reason, and report whether the subagent delivered a result.
            async Task<bool> RunTurnAsync(string message)
            {
                await foreach (var ev in loop.RunTurnAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    _observer.OnEvent(runId, ev);
                    switch (ev)
                    {
                        case ToolCallResultEvent r when r.ToolName == SubagentResultTool.ToolName && !r.IsError:
                            // The subagent reported back — stop consuming the turn here rather than
                            // letting it burn further iterations.
                            return true;
                        case AssistantMessageEvent a when !string.IsNullOrWhiteSpace(a.Content):
                            lastAssistant = a.Content;
                            break;
                        case TurnCompleteEvent c:
                            reason = c.Reason;
                            break;
                    }
                }
                return sink.HasResult;
            }

            var delivered = await RunTurnAsync(prompt).ConfigureAwait(false);

            // One-shot nudge: a subagent that stopped without delivering (typically it answered in
            // prose and forgot the protocol) gets exactly one reminder and one more turn to call
            // subagent_result. Skip it on cancellation — the caller asked to stop, so respect that.
            if (!delivered
                && reason != TurnCompletionReason.Cancelled
                && !cancellationToken.IsCancellationRequested)
            {
                delivered = await RunTurnAsync(NudgeMessage).ConfigureAwait(false);
            }

            outcome = delivered
                ? Delivered(sink, reason)
                : Mechanical(FailureMessage(def.Name, reason, lastAssistant), reason);
            return outcome;
        }
        finally
        {
            // Refresh with the full final context (the run has stopped, so the session is no longer
            // being mutated and is safe to read off this thread).
            PublishContext(runId, loop);
            _observer.OnCompleted(runId, outcome.IsFailure, outcome.Summary);
        }
    }

    // Snapshot the loop's next-request context for the observer. Best-effort: a serialization or
    // tool-schema hiccup must never derail the run or the completion callback.
    private void PublishContext(string runId, AgentLoop loop)
    {
        try
        {
            _observer.OnContext(runId, loop.BuildRequestPreview());
        }
        catch
        {
            // Diagnostic-only; ignore.
        }
    }

    private const string NudgeMessage =
        "You ended your turn without calling subagent_result, so the caller received nothing. You " +
        "MUST call subagent_result now — exactly once, with your status (success, failure, or " +
        "needs_followup) and your complete answer — to finish the run.";

    private static SubagentOutcome Mechanical(string message, TurnCompletionReason? reason = null) =>
        new(Delivered: false, SubagentStatus.Failure, message, MechanicalFailure: true, reason);

    private static SubagentOutcome Delivered(SubagentResultSink sink, TurnCompletionReason? reason) =>
        new(Delivered: true, sink.Status, sink.Result ?? "", MechanicalFailure: false, reason);

    private ChatSession BuildSession(
        SubagentDefinition def,
        ToolContext parentContext,
        IReadOnlyList<ITool> childTools,
        IPermissionResolver permissions)
    {
        var baseInstructions = string.IsNullOrWhiteSpace(def.Instructions)
            ? SubagentPrompts.DefaultInstructions
            : def.Instructions.Trim();

        // The fixed preamble leads, so it forms a stable prefix shared by every subagent run —
        // prefix-caching inference servers can reuse it regardless of which subagent (and which
        // per-subagent instructions) follows. SubagentPrompts.HeadlessPreamble is byte-stable.
        var systemPrompt = SubagentPrompts.HeadlessPreamble + "\n\n" + baseInstructions;

        // Let tools contribute their session-start state (e.g. the registered-scripts list), the
        // same way the parent chat builds its system message.
        var instructions = new List<string>();
        foreach (var tool in childTools)
        {
            var ctx = new ToolContext(parentContext.Workspace, permissions.RestrictToWorkspace(tool.Name));
            var contribution = tool.SessionStartMessage(ctx);
            if (!string.IsNullOrWhiteSpace(contribution))
                instructions.Add(contribution!);
        }

        return new ChatSession(parentContext.Workspace, systemPrompt, instructions);
    }

    private IChatCompletionClient? ResolveClient(SubagentDefinition def)
    {
        if (def.EndpointId is Guid id)
        {
            var endpoint = _settings.Endpoints.FindById(id);
            return endpoint is null ? null : _clientFactory.CreateCompletionClient(endpoint);
        }

        // Default: inherit the parent's client. Fall back to the configured default endpoint
        // when the runner was never bound (e.g. invoked outside a live chat).
        return _parentClient ?? _clientFactory.CreateCompletionClient(_settings.Endpoints.Default);
    }

    private static string FailureMessage(string name, TurnCompletionReason? reason, string lastAssistant)
    {
        var sb = new StringBuilder();
        sb.Append("Subagent '").Append(name).Append("' did not return a result");
        sb.Append(reason switch
        {
            TurnCompletionReason.MaxIterationsReached => " (it hit its tool-iteration cap).",
            TurnCompletionReason.Cancelled => " (it was cancelled).",
            _ => " (it stopped without calling subagent_result).",
        });

        if (!string.IsNullOrWhiteSpace(lastAssistant))
        {
            var tail = lastAssistant.Trim();
            if (tail.Length > FailureTailChars) tail = tail[..FailureTailChars] + "…";
            sb.Append(" Its last message was: ").Append(tail);
        }

        return sb.ToString();
    }

    private string AvailableAgentsHint()
    {
        var merged = SubagentRegistry.Merge(_settings.UserSubagents, _settings.ProjectSubagents);
        if (merged.Count == 0) return "No subagents are configured.";
        return "Available subagents: " + string.Join(", ", merged.Select(a => a.Name)) + ".";
    }
}
