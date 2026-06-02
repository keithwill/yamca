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
    private readonly Func<bool> _isYoloEnabled;
    private readonly SessionDiagnosticsLog? _diagnostics;

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
        AgentLoopOptions? options = null,
        Func<bool>? isYoloEnabled = null,
        SessionDiagnosticsLog? diagnostics = null)
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
        _isYoloEnabled = isYoloEnabled ?? (static () => false);
        _diagnostics = diagnostics;
    }

    private void Log(DiagnosticCategory category, string message) =>
        _diagnostics?.Log(category, message);

    /// <summary>Mirror the tool-related <see cref="ChatStreamEvent"/>s emitted by
    /// <see cref="HandleToolCallAsync"/> into the diagnostic log, so the timeline
    /// records every invocation, result, and denial alongside the model events.</summary>
    private void LogToolEvent(ChatStreamEvent ev)
    {
        if (_diagnostics is null) return;
        switch (ev)
        {
            case ToolCallStartedEvent s:
                Log(DiagnosticCategory.Tool, $"▶ {s.ToolName}({Preview(s.ArgumentsJson)})");
                break;
            case ToolCallResultEvent r:
                Log(r.IsError ? DiagnosticCategory.Error : DiagnosticCategory.Tool,
                    $"{(r.IsError ? "✗" : "✓")} {r.ToolName}: {r.Content.Length} chars");
                break;
            case ToolDeniedEvent d:
                Log(DiagnosticCategory.Error, $"⊘ denied {d.ToolName}: {Preview(d.Reason)}");
                break;
        }
    }

    private static string Preview(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    public ChatSession Session => _session;

    public IAsyncEnumerable<ChatStreamEvent> RunTurnAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userMessage);
        _session.AppendUser(userMessage);
        return RunLoopAsync(cancellationToken);
    }

    /// <summary>Resume the agent loop without appending a new user message. Used to
    /// continue a turn that previously stopped at the iteration cap: the conversation
    /// already ends with tool results the model has not yet responded to, so the loop
    /// simply picks up where it left off.</summary>
    public IAsyncEnumerable<ChatStreamEvent> ContinueTurnAsync(
        CancellationToken cancellationToken = default)
        => RunLoopAsync(cancellationToken);

    private async IAsyncEnumerable<ChatStreamEvent> RunLoopAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Rebuilt each iteration so live availability toggles on the Tools page take
            // effect. The set is intentionally cache-stable: deferred tools never enter it
            // (they are invoked via call_tool), so it does not grow as the model discovers
            // tools — which is what keeps the prompt prefix cache intact across a session.
            var chatTools = _tools.GetChatTools(_availability);

            // Signal the start of a model round-trip so the UI can show a prompt-processing
            // indicator during the silent gap before the first token arrives.
            yield return ModelRequestStartedEvent.Instance;

            Log(DiagnosticCategory.Request,
                $"→ model request (iteration {iteration + 1}/{_options.MaxIterations}, " +
                $"{_session.Messages.Count} msgs, {chatTools.Count} tools)");

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
                    case LlmToolCallStreamStarted:
                        Log(DiagnosticCategory.Model, "tool-call generation started");
                        yield return ToolCallGenerationStartedEvent.Instance;
                        break;
                    case LlmUsageUpdate usage:
                        Log(DiagnosticCategory.Usage,
                            $"usage: prompt={usage.PromptTokens}, completion={usage.CompletionTokens}" +
                            (usage.CachedTokens is int c ? $", cached={c}" : ""));
                        yield return new UsageUpdateEvent(usage.PromptTokens, usage.CompletionTokens, usage.CachedTokens);
                        break;
                    case LlmAssistantTurnComplete done:
                        content = done.Content;
                        toolCalls = done.ToolCalls;
                        var names = done.ToolCalls.Count > 0
                            ? " [" + string.Join(", ", done.ToolCalls.Select(t => t.ToolName)) + "]"
                            : "";
                        Log(DiagnosticCategory.Model,
                            $"assistant turn complete: finish_reason={done.FinishReason ?? "(none)"}, " +
                            $"content={done.Content.Length} chars, reasoning={done.Reasoning.Length} chars, " +
                            $"tool_calls={done.ToolCalls.Count}{names}");
                        break;
                }
            }

            _session.AppendAssistant(content, toolCalls);
            yield return new AssistantMessageEvent(content, toolCalls);

            if (toolCalls.Count == 0)
            {
                Log(DiagnosticCategory.Session, "turn complete: assistant reply");
                yield return new TurnCompleteEvent(TurnCompletionReason.AssistantReply);
                yield break;
            }

            foreach (var call in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await foreach (var ev in HandleToolCallAsync(call, cancellationToken).ConfigureAwait(false))
                {
                    LogToolEvent(ev);
                    yield return ev;
                }
            }
        }

        Log(DiagnosticCategory.Session, $"turn stopped: reached iteration cap ({_options.MaxIterations})");
        yield return new TurnCompleteEvent(TurnCompletionReason.MaxIterationsReached);
    }

    private async IAsyncEnumerable<ChatStreamEvent> HandleToolCallAsync(
        LlmToolCallRequest call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Unwrap the call_tool dispatcher to the real target. The tool result keeps the
        // dispatcher's CallId (so it references the assistant's call_tool tool_call), but all
        // routing, permissions, and UI events key on the inner tool. A plain (non-dispatch)
        // call leaves target == call.
        var viaDispatch = call.ToolName == CallToolTool.ToolName;
        LlmToolCallRequest target = call;
        if (viaDispatch)
        {
            var (ok, inner, error) = UnwrapDispatch(call);
            if (!ok)
            {
                _session.AppendToolResult(call.CallId, error!);
                yield return new ToolCallResultEvent(call.CallId, call.ToolName, IsError: true, Content: error!);
                yield break;
            }
            target = inner!;
        }

        var tool = _tools.Get(target.ToolName);
        var effective = tool is null ? Availability.Hidden : _availability.Resolve(tool.Name);

        if (tool is null || effective == Availability.Hidden)
        {
            var msg = viaDispatch
                ? $"Unknown deferred tool '{target.ToolName}'. Call lookup_tool with no arguments to see what is available."
                : $"Unknown tool '{target.ToolName}'.";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolDeniedEvent(call.CallId, target.ToolName, msg);
            yield break;
        }

        // Deferred tools must go through call_tool; a direct call by name (a model hallucination,
        // since deferred schemas are never in the prefix) is redirected rather than executed.
        if (effective == Availability.Deferred && !viaDispatch)
        {
            var msg = $"Tool '{target.ToolName}' is deferred. Read its schema with lookup_tool, then invoke it via call_tool(name=\"{target.ToolName}\", arguments={{...}}).";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolDeniedEvent(call.CallId, target.ToolName, msg);
            yield break;
        }

        // call_tool is only for deferred tools; dispatching an eager tool through it is a misuse.
        if (effective == Availability.Eager && viaDispatch)
        {
            var msg = $"'{target.ToolName}' is a regular tool — call it directly, not through call_tool.";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolCallResultEvent(call.CallId, target.ToolName, IsError: true, Content: msg);
            yield break;
        }

        // Self-correction: the first dispatch of a tool the model has not looked up returns the
        // schema instead of executing, so it can re-issue call_tool with valid arguments. Marking
        // it loaded means the retry (or any call after an explicit lookup_tool) executes directly.
        if (effective == Availability.Deferred && !_loadedTools.Contains(tool.Name))
        {
            _loadedTools.MarkLoaded(tool.Name);
            var schema = DeferredToolCatalog.Schemas(new[] { tool });
            var msg = $"Tool '{tool.Name}' was not loaded yet, so it was not executed. Here is its schema — re-issue call_tool with arguments matching it:\n{schema}";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolCallResultEvent(call.CallId, target.ToolName, IsError: true, Content: msg);
            yield break;
        }

        var (parsedOk, args, parseError) = TryParseArguments(target.ArgumentsJson);
        if (!parsedOk)
        {
            var msg = $"Invalid JSON arguments: {parseError}";
            _session.AppendToolResult(call.CallId, msg);
            yield return new ToolCallResultEvent(call.CallId, target.ToolName, IsError: true, Content: msg);
            yield break;
        }

        var level = _permissions.Resolve(target.ToolName);
        if (level == PermissionLevel.Ask)
        {
            // YOLO mode auto-accepts every approval prompt for the duration of the session,
            // without prompting or persisting the choice. An explicit Deny rule still denies.
            if (_isYoloEnabled())
            {
                level = PermissionLevel.Allow;
            }
            else
            {
                var decision = await _approvals.RequestApprovalAsync(target.ToolName, args, cancellationToken)
                                               .ConfigureAwait(false);

                var resolved = decision.Approved ? PermissionLevel.Allow : PermissionLevel.Deny;
                if (decision.Persistence != ApprovalPersistence.None)
                    _permissionStore.Persist(target.ToolName, resolved, decision.Persistence);

                level = resolved;
            }
        }

        if (level == PermissionLevel.Deny)
        {
            var reason = $"Permission denied for tool '{target.ToolName}'.";
            _session.AppendToolResult(call.CallId, reason);
            yield return new ToolDeniedEvent(call.CallId, target.ToolName, reason);
            yield break;
        }

        yield return new ToolCallStartedEvent(call.CallId, target.ToolName, target.ArgumentsJson);

        var context = new ToolContext(_workspace, _permissions.RestrictToWorkspace(target.ToolName));
        var result = await ExecuteToolSafelyAsync(tool, args, context, cancellationToken).ConfigureAwait(false);

        _session.AppendToolResult(call.CallId, result.Content);
        yield return new ToolCallResultEvent(call.CallId, target.ToolName, result.IsError, result.Content);
    }

    /// <summary>Parse a <c>call_tool</c> invocation into a synthetic request for the inner tool,
    /// preserving the dispatcher's <see cref="LlmToolCallRequest.CallId"/>. Accepts an
    /// <c>arguments</c> object (the normal case) or a JSON-encoded string (some models do this).</summary>
    private static (bool ok, LlmToolCallRequest? inner, string? error) UnwrapDispatch(LlmToolCallRequest call)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (false, null, $"Invalid JSON arguments for call_tool: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("name", out var nameEl) ||
            nameEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            return (false, null, "call_tool requires a string 'name' identifying the deferred tool to invoke.");
        }

        var innerName = nameEl.GetString()!;
        var innerArgs = "{}";
        if (root.TryGetProperty("arguments", out var argsEl))
        {
            switch (argsEl.ValueKind)
            {
                case JsonValueKind.Object:
                    innerArgs = argsEl.GetRawText();
                    break;
                case JsonValueKind.String:
                    innerArgs = argsEl.GetString() ?? "{}";
                    break;
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;
                default:
                    return (false, null, "call_tool 'arguments' must be an object matching the target tool's schema.");
            }
        }

        return (true, new LlmToolCallRequest(call.CallId, innerName, innerArgs), null);
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
