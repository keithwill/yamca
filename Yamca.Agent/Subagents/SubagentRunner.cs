using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yamca.Agent.Chat;
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
    private readonly IHttpClientFactory _httpFactory;
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
        IHttpClientFactory httpFactory,
        ISubagentObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(httpFactory);
        _settings = settings;
        _services = services;
        _approvals = approvals;
        _httpFactory = httpFactory;
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
        ArgumentNullException.ThrowIfNull(parentContext);

        var def = SubagentRegistry.Resolve(_settings.UserSubagents, _settings.ProjectSubagents, agentName);
        if (def is null)
            return ToolResult.Error($"Unknown subagent '{agentName}'. {AvailableAgentsHint()}");

        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("A non-empty 'prompt' is required to run a subagent.");

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
                return ToolResult.Error($"Subagent '{def.Name}' was not approved to run.");
        }

        var client = ResolveClient(def);
        if (client is null)
            return ToolResult.Error(
                $"Subagent '{def.Name}' references an endpoint that no longer exists and no parent endpoint is available.");

        // Build the subagent's private tool set: its allowed tools (sourced from the parent
        // registry snapshot) plus the result tool. subagent_run is deliberately never included,
        // so a subagent cannot spawn further subagents.
        var registry = (IToolRegistry?)_services.GetService(typeof(IToolRegistry))
            ?? throw new InvalidOperationException("IToolRegistry is not registered.");

        var sink = new SubagentResultSink();
        var allowed = new HashSet<string>(def.AllowedTools, StringComparer.Ordinal);
        var childTools = registry.Tools
            .Where(t => allowed.Contains(t.Name) && t.Name != SubagentRunTool.ToolName)
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
        // transcript from the subagent_run card. OnCompleted always fires (see finally).
        var runId = Guid.NewGuid().ToString("n");
        _observer.OnStarted(new SubagentRunInfo(
            runId, parentContext.CallId, parentContext.OwnerId, def.Name, prompt, DateTimeOffset.Now));

        var outcome = ToolResult.Error(FailureMessage(def.Name, null, ""));
        try
        {
            var lastAssistant = "";
            TurnCompletionReason? reason = null;

            await foreach (var ev in loop.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false))
            {
                _observer.OnEvent(runId, ev);
                switch (ev)
                {
                    case ToolCallResultEvent r when r.ToolName == SubagentResultTool.ToolName && !r.IsError:
                        // The subagent reported back — stop the loop here rather than letting it
                        // burn further iterations, and hand the payload to the caller.
                        outcome = ToolResult.Ok(sink.Result ?? "");
                        return outcome;
                    case AssistantMessageEvent a when !string.IsNullOrWhiteSpace(a.Content):
                        lastAssistant = a.Content;
                        break;
                    case TurnCompleteEvent c:
                        reason = c.Reason;
                        break;
                }
            }

            // Defensive: if the result landed but we somehow fell out of the loop, still return it.
            outcome = sink.HasResult
                ? ToolResult.Ok(sink.Result ?? "")
                : ToolResult.Error(FailureMessage(def.Name, reason, lastAssistant));
            return outcome;
        }
        finally
        {
            _observer.OnCompleted(runId, outcome.IsError, outcome.Content);
        }
    }

    private ChatSession BuildSession(
        SubagentDefinition def,
        ToolContext parentContext,
        IReadOnlyList<ITool> childTools,
        IPermissionResolver permissions)
    {
        var baseInstructions = string.IsNullOrWhiteSpace(def.Instructions)
            ? "You are a focused subagent that completes a single delegated task."
            : def.Instructions.Trim();

        var systemPrompt = baseInstructions +
            "\n\nYou are running headless as a subagent — there is no user to talk to. When you " +
            "have finished, call the subagent_result tool exactly once with your complete answer; " +
            "that is the only output the caller receives. Do not ask clarifying questions: make " +
            "reasonable assumptions and proceed. Your responses are not rendered as Markdown.";

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
            return endpoint is null ? null : BuildClient(endpoint);
        }

        // Default: inherit the parent's client. Fall back to the configured default endpoint
        // when the runner was never bound (e.g. invoked outside a live chat).
        return _parentClient ?? BuildClient(_settings.Endpoints.Default);
    }

    private IChatCompletionClient BuildClient(EndpointSettings endpoint)
    {
        var modelId = string.IsNullOrWhiteSpace(endpoint.Model) ? "local-model" : endpoint.Model;
        var baseUrl = endpoint.BaseUrl.EndsWith('/') ? endpoint.BaseUrl : endpoint.BaseUrl + "/";

        var http = _httpFactory.CreateClient("yamca-llm");
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(endpoint.ApiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        http.Timeout = Timeout.InfiniteTimeSpan;

        return new OpenAIChatCompletionClient(http, modelId);
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
