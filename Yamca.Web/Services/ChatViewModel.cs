using System.Net.Http.Headers;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Per-circuit chat state and orchestrator. Owns the <see cref="AgentLoop"/>
/// (lazily constructed on first send so localStorage settings are loaded first),
/// the visible turn log, and the queue of outstanding approval prompts.</summary>
public sealed class ChatViewModel : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly IToolRegistry _tools;
    private readonly IPermissionResolver _permissions;
    private readonly IApprovalCoordinator _approvals;
    private readonly IPermissionStore _permissionStore;
    private readonly SessionSettings _settings;
    private readonly InstructionFilesLoader _instructionLoader;
    private readonly IHttpClientFactory _httpFactory;
    private readonly EndpointHealthService _endpointHealth;

    private AgentLoop? _loop;
    private CancellationTokenSource? _runCts;
    private Task? _approvalConsumer;
    private CancellationTokenSource? _consumerCts;
    private int? _lastReportedPromptTokens;
    private int? _lastReportedCompletionTokens;

    public ChatViewModel(
        IWorkspace workspace,
        IToolRegistry tools,
        IPermissionResolver permissions,
        IApprovalCoordinator approvals,
        IPermissionStore permissionStore,
        SessionSettings settings,
        InstructionFilesLoader instructionLoader,
        IHttpClientFactory httpFactory,
        EndpointHealthService endpointHealth)
    {
        _workspace = workspace;
        _tools = tools;
        _permissions = permissions;
        _approvals = approvals;
        _permissionStore = permissionStore;
        _settings = settings;
        _instructionLoader = instructionLoader;
        _httpFactory = httpFactory;
        _endpointHealth = endpointHealth;
    }

    public List<ChatTurn> Turns { get; } = new();
    public List<PendingApproval> Approvals { get; } = new();
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Best-available count of input tokens currently in the conversation.
    /// Prefers the server-reported <c>prompt_tokens</c> from the last streaming
    /// usage chunk (accurate, but lags new messages until the next response).
    /// Falls back to the char/4 estimate while a turn is in flight or before any
    /// usage has been reported. Whichever is larger wins, so the badge only
    /// climbs.</summary>
    public int CurrentContextTokens
    {
        get
        {
            var estimate = _loop?.Session.EstimatedInputTokens ?? 0;
            var reported = _lastReportedPromptTokens ?? 0;
            return estimate > reported ? estimate : reported;
        }
    }

    /// <summary>Completion tokens reported by the server for the most recent
    /// assistant turn. Null until the first <see cref="UsageUpdateEvent"/>.</summary>
    public int? LastCompletionTokens => _lastReportedCompletionTokens;

    /// <summary>Configured context window reported by the backend at connect time
    /// (llama.cpp <c>/props</c> or vLLM <c>/v1/models</c>). Null when the backend
    /// did not expose this — UI falls back to a single-number badge.</summary>
    public int? MaxContextTokens { get; private set; }

    /// <summary>Short label for the source of <see cref="MaxContextTokens"/>, for tooltips.</summary>
    public string? MaxContextSource { get; private set; }

    /// <summary>Fired on any state mutation. The Blazor page hooks this and calls
    /// <c>InvokeAsync(StateHasChanged)</c> to re-render on the renderer dispatcher.</summary>
    public event Action? Changed;

    public async Task SendAsync(string prompt)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(prompt)) return;

        EnsureStarted();

        var turn = new ChatTurn(prompt);
        Turns.Add(turn);

        IsRunning = true;
        Error = null;
        Raise();

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            await foreach (var ev in _loop!.RunTurnAsync(prompt, ct).ConfigureAwait(false))
            {
                Apply(turn, ev);
                Raise();
            }
        }
        catch (OperationCanceledException)
        {
            turn.Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            turn.Error = ex.Message;
            Error = ex.Message;
        }
        finally
        {
            turn.IsRunning = false;
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            Raise();
        }
    }

    public void Cancel()
    {
        _runCts?.Cancel();
    }

    /// <summary>Resolve an outstanding approval. The agent loop is awaiting the
    /// underlying <see cref="ApprovalRequest"/>; this completes its task and
    /// drops the prompt from the visible queue.</summary>
    public void ResolveApproval(PendingApproval approval, bool approved, ApprovalPersistence persistence)
    {
        if (approved) approval.Request.Approve(persistence);
        else approval.Request.Deny(persistence);

        Approvals.Remove(approval);
        Raise();
    }

    /// <summary>"Allow and register" flow for execute_discovered_script: add the script
    /// to the project-tier registry, then resolve the approval as Allow (no permission
    /// persistence — registration is the persistence mechanism for script tools).</summary>
    public void RegisterAndAllow(PendingApproval approval, string? description)
    {
        var path = approval.ScriptPath;
        if (path is null)
        {
            // Should not happen — the UI only shows the button when ScriptPath is non-null.
            ResolveApproval(approval, approved: true, ApprovalPersistence.None);
            return;
        }

        var normalized = path.Trim().Replace('\\', '/');
        _settings.AddRegisteredScript(SettingsTier.Project, new RegisteredScript(normalized, string.IsNullOrWhiteSpace(description) ? null : description.Trim()));

        ResolveApproval(approval, approved: true, ApprovalPersistence.None);
    }

    public void Clear()
    {
        if (IsRunning) Cancel();
        Turns.Clear();
        Approvals.Clear();
        _loop = null;     // forces a fresh ChatSession (system prompt re-rendered) on next send
        _lastReportedPromptTokens = null;
        _lastReportedCompletionTokens = null;
        Error = null;
        Raise();
    }

    private void EnsureStarted()
    {
        if (_loop is not null) return;

        var endpoint = _settings.Endpoint;
        var modelId = string.IsNullOrWhiteSpace(endpoint.Model) ? "local-model" : endpoint.Model;
        var baseUrl = endpoint.BaseUrl.EndsWith('/') ? endpoint.BaseUrl : endpoint.BaseUrl + "/";

        var http = _httpFactory.CreateClient("yamca-llm");
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(endpoint.ApiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        http.Timeout = Timeout.InfiniteTimeSpan;

        var completion = new OpenAIChatCompletionClient(http, modelId);

        var prompt = _settings.SystemPrompt;
        var hint = _settings.MarkdownEnabled
            ? "Your responses are rendered as GitHub-flavored Markdown — use fenced code blocks for code, and standard Markdown for emphasis, lists, and tables."
            : "Your responses are rendered as plain text. Do NOT use Markdown formatting: no `backticks`, no **bold**/*italics*, no #headings, no fenced code blocks, no bullet/numbered lists. Write code and identifiers inline as plain text.";
        prompt = (string.IsNullOrWhiteSpace(prompt) ? "" : prompt + "\n\n") + hint;
        var instructions = _instructionLoader.Load(_settings, _workspace).ToList();
        foreach (var tool in _tools.Tools)
        {
            var ctx = new ToolContext(_workspace, _permissions.RestrictToWorkspace(tool.Name));
            var contribution = tool.SessionStartMessage(ctx);
            if (!string.IsNullOrWhiteSpace(contribution))
                instructions.Add(contribution);
        }
        var session = new ChatSession(_workspace, prompt, instructions);

        _loop = new AgentLoop(
            session, completion, _tools, _permissions, _approvals, _permissionStore, _workspace);

        StartApprovalConsumer();
        _ = DetectCapabilitiesAsync(endpoint);
    }

    private async Task DetectCapabilitiesAsync(EndpointSettings endpoint)
    {
        // Best-effort probe at connect time. Any failure leaves the badge in
        // single-number (estimate-only) mode — never block the user.
        try
        {
            var caps = await _endpointHealth.DetectCapabilitiesAsync(endpoint).ConfigureAwait(false);
            if (caps.MaxContextTokens is > 0)
            {
                MaxContextTokens = caps.MaxContextTokens;
                MaxContextSource = caps.Source;
                Raise();
            }
        }
        catch { /* capability detection is non-essential */ }
    }

    private void StartApprovalConsumer()
    {
        if (_approvalConsumer is not null) return;

        _consumerCts = new CancellationTokenSource();
        var ct = _consumerCts.Token;
        _approvalConsumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var request in _approvals.Pending.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    Approvals.Add(new PendingApproval(request));
                    Raise();
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }, ct);
    }

    private void Apply(ChatTurn turn, ChatStreamEvent ev)
    {
        switch (ev)
        {
            case AssistantTokenEvent token:
                var text = CurrentOrNewText(turn);
                text.Append(token.Delta);
                break;

            case ReasoningTokenEvent rtoken:
                var rItem = CurrentOrNewReasoning(turn);
                rItem.Append(rtoken.Delta);
                break;

            case ReasoningCompleteEvent:
                var openR = CurrentReasoning(turn);
                if (openR is not null) openR.IsComplete = true;
                break;

            case AssistantMessageEvent msg:
                // The streaming buffer already holds the same content; just mark complete.
                var current = CurrentText(turn);
                if (current is null && !string.IsNullOrEmpty(msg.Content))
                {
                    var t = new AssistantTextItem();
                    t.Append(msg.Content);
                    turn.Items.Add(t);
                    current = t;
                }
                if (current is not null) current.IsComplete = true;
                break;

            case ToolCallStartedEvent started:
                turn.Items.Add(new ToolCallItem
                {
                    CallId = started.CallId,
                    ToolName = started.ToolName,
                    ArgumentsJson = started.ArgumentsJson,
                    State = ToolCallState.Pending,
                });
                break;

            case ToolCallResultEvent done:
                if (TryFind(turn, done.CallId, out var doneItem))
                {
                    doneItem.State = done.IsError ? ToolCallState.Failed : ToolCallState.Succeeded;
                    doneItem.Result = done.Content;
                }
                break;

            case ToolDeniedEvent denied:
                if (TryFind(turn, denied.CallId, out var dItem))
                {
                    dItem.State = ToolCallState.Denied;
                    dItem.Result = denied.Reason;
                }
                else
                {
                    // Some denial paths (unknown tool, malformed JSON) skip the
                    // ToolCallStartedEvent and only emit the denied/result event.
                    turn.Items.Add(new ToolCallItem
                    {
                        CallId = denied.CallId,
                        ToolName = denied.ToolName,
                        ArgumentsJson = "",
                        State = ToolCallState.Denied,
                        Result = denied.Reason,
                    });
                }
                break;

            case UsageUpdateEvent usage:
                _lastReportedPromptTokens = usage.PromptTokens;
                _lastReportedCompletionTokens = usage.CompletionTokens;
                break;

            case TurnCompleteEvent: /* nothing to do — finally{} handles it */ break;
        }
    }

    private static AssistantTextItem CurrentOrNewText(ChatTurn turn)
    {
        if (turn.Items.LastOrDefault() is AssistantTextItem t && !t.IsComplete) return t;
        var fresh = new AssistantTextItem();
        turn.Items.Add(fresh);
        return fresh;
    }

    private static AssistantTextItem? CurrentText(ChatTurn turn) =>
        turn.Items.OfType<AssistantTextItem>().LastOrDefault(t => !t.IsComplete);

    private static ReasoningItem CurrentOrNewReasoning(ChatTurn turn)
    {
        if (turn.Items.LastOrDefault() is ReasoningItem r && !r.IsComplete) return r;
        var fresh = new ReasoningItem();
        turn.Items.Add(fresh);
        return fresh;
    }

    private static ReasoningItem? CurrentReasoning(ChatTurn turn) =>
        turn.Items.OfType<ReasoningItem>().LastOrDefault(r => !r.IsComplete);

    private static bool TryFind(ChatTurn turn, string callId, out ToolCallItem item)
    {
        item = turn.Items.OfType<ToolCallItem>().FirstOrDefault(c => c.CallId == callId)!;
        return item is not null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        try { _runCts?.Cancel(); } catch { /* ignore */ }
        try { _consumerCts?.Cancel(); } catch { /* ignore */ }
        _runCts?.Dispose();
        _consumerCts?.Dispose();
    }
}
