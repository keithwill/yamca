using System.Net.Http.Headers;
using Yamca.Agent.Chat;
using Yamca.Agent.Git;
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
    private readonly IAvailabilityResolver _availability;
    private readonly IApprovalCoordinator _approvals;
    private readonly IPermissionStore _permissionStore;
    private readonly SessionSettings _settings;
    private readonly InstructionFilesLoader _instructionLoader;
    private readonly IHttpClientFactory _httpFactory;
    private readonly EndpointHealthService _endpointHealth;
    private readonly LoadedToolSet _loadedTools;
    private readonly ContextCompactor _compactor;

    private AgentLoop? _loop;
    private readonly List<string> _seedInstructions = new();
    private CancellationTokenSource? _runCts;
    private Task? _approvalConsumer;
    private CancellationTokenSource? _consumerCts;
    private int? _lastReportedPromptTokens;
    private int? _lastReportedCompletionTokens;

    public ChatViewModel(
        int id,
        IWorkspace workspace,
        IToolRegistry tools,
        IPermissionResolver permissions,
        IAvailabilityResolver availability,
        IApprovalCoordinator approvals,
        IPermissionStore permissionStore,
        SessionSettings settings,
        InstructionFilesLoader instructionLoader,
        IHttpClientFactory httpFactory,
        EndpointHealthService endpointHealth,
        LoadedToolSet loadedTools,
        ContextCompactor compactor)
    {
        Id = id;
        _workspace = workspace;
        _tools = tools;
        _permissions = permissions;
        _availability = availability;
        _approvals = approvals;
        _permissionStore = permissionStore;
        _settings = settings;
        _instructionLoader = instructionLoader;
        _httpFactory = httpFactory;
        _endpointHealth = endpointHealth;
        _loadedTools = loadedTools;
        _compactor = compactor;
    }

    public int Id { get; }

    /// <summary>User's preferred endpoint id for this chat. Settable while the chat
    /// has no turns; ignored once <see cref="LockedEndpoint"/> is set. <c>null</c>
    /// means "use whatever is configured as Default at first-send time".</summary>
    public Guid? SelectedEndpointId { get; private set; }

    /// <summary>Endpoint snapshot taken on the first send. Survives later edits or
    /// deletion of the endpoint in Settings so an active chat keeps working.</summary>
    public EndpointSettings? LockedEndpoint { get; private set; }

    public bool IsEndpointLocked => LockedEndpoint is not null;

    /// <summary>Sets the user's endpoint pick for this chat. Caller is responsible
    /// for refusing the call once <see cref="IsEndpointLocked"/> is true; we just
    /// ignore it to keep the lock invariant.</summary>
    public void SelectEndpoint(Guid id)
    {
        if (IsEndpointLocked) return;
        if (SelectedEndpointId == id) return;
        SelectedEndpointId = id;
        Raise();
    }

    /// <summary>Extra system instructions injected into the first turn's system message,
    /// in addition to the configured instruction files. Used to seed a session launched for
    /// a board step with that column's instructions.md. No-op once the session has started
    /// (the system message is built lazily on first send).</summary>
    public void AddSeedInstruction(string? text)
    {
        if (_loop is not null) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _seedInstructions.Add(text);
    }

    /// <summary>One-shot composer pre-fill consumed by <c>ChatSessionPanel</c> when it binds
    /// this session. Lets a board step seed a prompt the user reviews before sending. Cleared
    /// by the panel once read.</summary>
    public string? DraftPrompt { get; set; }

    /// <summary>Set when this session is bound to a git worktree. Drives the
    /// Merge / Delete branch toolbar buttons and the tile-header branch label.</summary>
    public WorktreeInfo? WorktreeInfo { get; private set; }

    /// <summary>Cached result of probing the workspace for a <c>.git</c> dir.
    /// The branch toolbar button uses this to gate enablement.</summary>
    public bool? IsGitRepository { get; set; }

    public IWorkspace Workspace => _workspace;

    internal void BindWorktree(WorktreeInfo info) => WorktreeInfo = info;

    public string Title
    {
        get
        {
            var first = Turns.FirstOrDefault()?.UserMessage;
            if (string.IsNullOrWhiteSpace(first))
                return WorktreeInfo?.Branch ?? "New Chat";
            var trimmed = first.Trim().ReplaceLineEndings(" ");
            return trimmed.Length <= 32 ? trimmed : trimmed[..32] + "…";
        }
    }

    public List<ChatTurn> Turns { get; } = new();
    public List<PendingApproval> Approvals { get; } = new();
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    /// <summary>True while either an agent turn is running or a (manual or
    /// auto) compaction is in flight. UI uses this to gate the composer so the
    /// user can't start a second concurrent operation against the session.</summary>
    public bool IsBusy => IsRunning || IsCompacting;

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

    /// <summary>True while a summarization round-trip is in flight. The chat panel
    /// uses this to surface a snackbar so the user knows the next turn is delayed
    /// for a reason.</summary>
    public bool IsCompacting { get; private set; }

    /// <summary>Set when the most recent compaction failed; cleared on the next
    /// successful compaction or chat clear.</summary>
    public string? CompactionError { get; private set; }

    /// <summary>UI turn index above which an "earlier turns summarized" divider
    /// should be drawn. Updated after each successful compaction.</summary>
    public int? CompactionBoundaryUiTurnIndex { get; private set; }

    /// <summary>Text of the most recent compaction summary. The divider in the
    /// chat panel is clickable to surface this so the user can verify what was
    /// preserved (and what was lost) across the boundary. Replaced on each new
    /// compaction; cleared on chat reset.</summary>
    public string? LastCompactionSummary { get; private set; }

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

            await MaybeCompactAsync(ct).ConfigureAwait(false);
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
        _seedInstructions.Clear();
        LockedEndpoint = null;  // a cleared chat is a fresh chat — re-pick endpoint on next send
        _lastReportedPromptTokens = null;
        _lastReportedCompletionTokens = null;
        Error = null;
        IsCompacting = false;
        CompactionError = null;
        CompactionBoundaryUiTurnIndex = null;
        LastCompactionSummary = null;
        Raise();
    }

    private async Task MaybeCompactAsync(CancellationToken ct)
    {
        if (!_settings.AutoCompactionEnabled) return;
        if (MaxContextTokens is not > 0) return;
        if (_loop is null || LockedEndpoint is null) return;

        var used = CurrentContextTokens;
        var ratio = (double)used / MaxContextTokens.Value;
        if (ratio < _settings.AutoCompactionThresholdPercent / 100.0) return;

        var keepTurns = _settings.AutoCompactionKeepRecentTurns;
        var keepFrom = _loop.Session.FindKeepFromIndexForRecentTurns(keepTurns);
        if (keepFrom < 0) return; // not enough turns yet

        // Inside SendAsync's try{} so OperationCanceledException bubbles to its
        // existing cancel handler and any other exception lands in CompactionError.
        await RunCompactionAsync(keepTurns, keepFrom, ct).ConfigureAwait(false);
    }

    /// <summary>Manual compaction triggered from the composer. Returns <c>null</c> on
    /// success (or after a recoverable no-op), or a human-readable reason when the
    /// request couldn't even start. Failures during the LLM round-trip surface via
    /// <see cref="CompactionError"/> and the existing snackbar wiring.</summary>
    public async Task<string?> CompactNowAsync(int keepRecentTurns)
    {
        if (IsBusy) return "Chat is busy — wait for the current turn to finish.";
        if (_loop is null || LockedEndpoint is null) return "Send a message first — there's nothing to compact yet.";

        if (keepRecentTurns < 1) keepRecentTurns = 1;

        var keepFrom = _loop.Session.FindKeepFromIndexForRecentTurns(keepRecentTurns);
        if (keepFrom < 0)
            return $"Not enough earlier turns to summarize — need more than {keepRecentTurns} user message(s) in history.";

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            await RunCompactionAsync(keepRecentTurns, keepFrom, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // Cancellation already snackbar'd via CompactionError or treated as quiet stop.
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task RunCompactionAsync(int keepTurns, int keepFromMessageIndex, CancellationToken ct)
    {
        IsCompacting = true;
        CompactionError = null;
        Raise();
        try
        {
            var summary = await _compactor
                .SummarizeAsync(_loop!.Session, LockedEndpoint!, keepFromMessageIndex, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                _loop.Session.Compact(summary, keepFromMessageIndex);
                _lastReportedPromptTokens = null;
                CompactionBoundaryUiTurnIndex = Math.Max(0, Turns.Count - keepTurns);
                LastCompactionSummary = summary;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CompactionError = ex.Message;
        }
        finally
        {
            IsCompacting = false;
            Raise();
        }
    }

    private void EnsureStarted()
    {
        if (_loop is not null) return;

        // Snapshot the chosen endpoint so later edits/deletes in Settings don't
        // disrupt this chat. Once locked, every subsequent turn uses the snapshot.
        var endpoints = _settings.Endpoints;
        var resolved = SelectedEndpointId is Guid id ? endpoints.FindById(id) : null;
        var endpoint = resolved ?? endpoints.Default;
        LockedEndpoint = endpoint;

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
        instructions.AddRange(_seedInstructions);
        var session = new ChatSession(_workspace, prompt, instructions);

        _loop = new AgentLoop(
            session, completion, _tools, _permissions, _availability, _approvals, _permissionStore, _workspace, _loadedTools);

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
