using System.Runtime.CompilerServices;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Orchestrates a single user turn: send messages to the LLM, stream tokens
/// back, handle tool calls (with permission gating), and loop until the assistant
/// produces a plain reply or the iteration cap is reached.</summary>
public sealed class AgentLoop
{
    private readonly ChatSession _session;
    private readonly IChatCompletionClient _client;
    private readonly IToolRegistry _tools;
    private readonly IPermissionResolver _permissions;
    private readonly IAvailabilityResolver _availability;
    private readonly IApprovalCoordinator _approvals;
    private readonly IPermissionStore _permissionStore;
    private readonly IWorkspace _workspace;
    private readonly LoadedToolSet _loadedTools;
    private readonly AgentLoopOptions _options;

    public AgentLoop(
        ChatSession session,
        IChatCompletionClient client,
        IToolRegistry tools,
        IPermissionResolver permissions,
        IAvailabilityResolver availability,
        IApprovalCoordinator approvals,
        IPermissionStore permissionStore,
        IWorkspace workspace,
        LoadedToolSet loadedTools,
        AgentLoopOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(permissionStore);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(loadedTools);

        _session = session;
        _client = client;
        _tools = tools;
        _permissions = permissions;
        _availability = availability;
        _approvals = approvals;
        _permissionStore = permissionStore;
        _workspace = workspace;
        _loadedTools = loadedTools;
        _options = options ?? AgentLoopOptions.Default;
    }

    public ChatSession Session => _session;

    public async IAsyncEnumerable<ChatStreamEvent> RunTurnAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userMessage);
        _session.AppendUser(userMessage);

        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Rebuild each iteration so schemas loaded by load_tool mid-turn become
            // visible to the LLM on the very next round-trip. The resolver is also
            // queried per iteration so user toggles on the Tools page take effect live.
            var chatTools = _tools.GetChatTools(_loadedTools, _availability);

            string content = "";
            IReadOnlyList<LlmToolCallRequest> toolCalls = Array.Empty<LlmToolCallRequest>();

            await foreach (var ev in _client.StreamAsync(_session.Messages, chatTools, cancellationToken)
                                            .ConfigureAwait(false))
            {
                switch (ev)
                {
                    case LlmContentDelta delta:
                        yield return new AssistantTokenEvent(delta.Text);
                        break;
                    case LlmReasoningDelta rdelta:
                        yield return new ReasoningTokenEvent(rdelta.Text);
                        break;
                    case LlmReasoningClose:
                        yield return ReasoningCompleteEvent.Instance;
                        break;
                    case LlmUsageUpdate usage:
                        yield return new UsageUpdateEvent(usage.PromptTokens, usage.CompletionTokens, usage.CachedTokens);
                        break;
                    case LlmAssistantTurnComplete done:
                        content = done.Content;
                        toolCalls = done.ToolCalls;
                        break;
                }
            }

            _session.AppendAssistant(content, toolCalls);
            yield return new AssistantMessageEvent(content, toolCalls);

            if (toolCalls.Count == 0)
            {
                yield return new TurnCompleteEvent(TurnCompletionReason.AssistantReply);
                yield break;
            }

            foreach (var call in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await foreach (var ev in HandleToolCallAsync(call, cancellationToken).ConfigureAwait(false))
                    yield return ev;
            }
        }

        yield return new TurnCompleteEvent(TurnCompletionReason.MaxIterationsReached);
    }

    private async IAsyncEnumerable<ChatStreamEvent> HandleToolCallAsync(
        LlmToolCallRequest call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tool = _tools.Get(call.ToolName);
        if (tool is null)
        {
            var msg = $"Unknown tool '{call.ToolName}'.";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolDeniedEvent(call.CallId, call.ToolName, msg);
            yield break;
        }

        var effective = _availability.Resolve(tool.Name);
        if (effective == Availability.Hidden)
        {
            var msg = $"Unknown tool '{call.ToolName}'.";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolDeniedEvent(call.CallId, call.ToolName, msg);
            yield break;
        }
        if (effective == Availability.Deferred && !_loadedTools.Contains(tool.Name))
        {
            var msg = $"Tool '{call.ToolName}' is deferred and has not been loaded. Call load_tool with tool_names=[\"{call.ToolName}\"] first.";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolDeniedEvent(call.CallId, call.ToolName, msg);
            yield break;
        }

        var (parsedOk, args, parseError) = TryParseArguments(call.ArgumentsJson);
        if (!parsedOk)
        {
            var msg = $"Invalid JSON arguments: {parseError}";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolCallResultEvent(call.CallId, call.ToolName, IsError: true, Content: msg);
            yield break;
        }

        var level = _permissions.Resolve(call.ToolName);
        if (level == PermissionLevel.Ask)
        {
            var decision = await _approvals.RequestApprovalAsync(call.ToolName, args, cancellationToken)
                                           .ConfigureAwait(false);

            var resolved = decision.Approved ? PermissionLevel.Allow : PermissionLevel.Deny;
            if (decision.Persistence != ApprovalPersistence.None)
                _permissionStore.Persist(call.ToolName, resolved, decision.Persistence);

            level = resolved;
        }

        if (level == PermissionLevel.Deny)
        {
            var reason = $"Permission denied for tool '{call.ToolName}'.";
            _session.AppendToolResult(call.CallId, reason);
            yield return new ToolDeniedEvent(call.CallId, call.ToolName, reason);
            yield break;
        }

        yield return new ToolCallStartedEvent(call.CallId, call.ToolName, call.ArgumentsJson);

        var context = new ToolContext(_workspace, _permissions.RestrictToWorkspace(call.ToolName));
        var result = await ExecuteToolSafelyAsync(tool, args, context, cancellationToken).ConfigureAwait(false);

        _session.AppendToolResult(call.CallId, result.Content);
        yield return new ToolCallResultEvent(call.CallId, call.ToolName, result.IsError, result.Content);
    }

    private static async Task<ToolResult> ExecuteToolSafelyAsync(
        ITool tool, JsonElement args, ToolContext context, CancellationToken ct)
    {
        try
        {
            return await tool.ExecuteAsync(args, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Tool '{tool.Name}' threw: {ex.Message}");
        }
    }

    private static (bool ok, JsonElement element, string? error) TryParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (true, JsonDocument.Parse("{}").RootElement.Clone(), null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (true, doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (false, default, ex.Message);
        }
    }

}
