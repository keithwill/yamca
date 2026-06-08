using Yamca.Agent.Chat;
using Yamca.Agent.Chat.Persistence;
using Yamca.Agent.Chat.Prompts;
using Yamca.Agent.Git;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Per-circuit chat state and orchestrator. Owns the <see cref="AgentLoop"/>
/// (lazily constructed on first send so persisted settings are loaded first),
/// the visible turn log, and the queue of outstanding approval prompts.</summary>
public sealed class ChatViewModel : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly IToolRegistry _tools;
    private readonly IPermissionResolver _permissions;
    private readonly IApprovalCoordinator _approvals;
    private readonly SessionSettings _settings;
    private readonly InstructionFilesLoader _instructionLoader;
    private readonly EndpointClientFactory _clientFactory;
    private readonly EndpointHealthService _endpointHealth;
    private readonly AgentLoopFactory _loopFactory;
    private readonly ContextCompactor _compactor;
    private readonly ChatStore _store;
    private readonly ISubagentRunner _subagentRunner;
    private readonly SessionDiagnosticsLog _diagnostics = new();

    private AgentLoop? _loop;
    private CancellationTokenSource? _runCts;
    private Task? _approvalConsumer;
    private CancellationTokenSource? _consumerCts;
    private int? _lastReportedPromptTokens;
    private int? _lastReportedCompletionTokens;

    // Set when this VM was loaded from a saved session and hasn't sent a turn yet.
    // Consumed by EnsureStarted to rebuild the agent loop from the saved state instead
    // of a fresh system prompt.
    private IReadOnlyList<ChatMessage>? _restoredMessages;
    private PersistedEndpoint? _restoredEndpoint;
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    public ChatViewModel(
        int id,
        IWorkspace workspace,
        IToolRegistry tools,
        IPermissionResolver permissions,
        IApprovalCoordinator approvals,
        SessionSettings settings,
        InstructionFilesLoader instructionLoader,
        EndpointClientFactory clientFactory,
        EndpointHealthService endpointHealth,
        AgentLoopFactory loopFactory,
        ContextCompactor compactor,
        ChatStore store,
        ISubagentRunner subagentRunner)
    {
        Id = id;
        _workspace = workspace;
        _tools = tools;
        _permissions = permissions;
        _approvals = approvals;
        _settings = settings;
        _instructionLoader = instructionLoader;
        _clientFactory = clientFactory;
        _endpointHealth = endpointHealth;
        _loopFactory = loopFactory;
        _compactor = compactor;
        _store = store;
        _subagentRunner = subagentRunner;
    }

    public int Id { get; }

    /// <summary>In-memory diagnostic timeline for this session — model round-trips
    /// (with <c>finish_reason</c>), tool execution, and lifecycle events. Surfaced in
    /// the composer's "Diagnostic Log" dialog. Lives for the lifetime of the VM and is
    /// reset by <see cref="Clear"/>.</summary>
    public SessionDiagnosticsLog Diagnostics => _diagnostics;

    /// <summary>Stable identifier used as the persistence file name and to detect when a
    /// saved chat is already open. Distinct from the volatile 1–4 slot <see cref="Id"/>.
    /// Rotated by <see cref="Clear"/> so a cleared chat becomes a new history entry.</summary>
    public Guid PersistentId { get; private set; } = Guid.NewGuid();

    /// <summary>Timestamp of the most recent turn start or completion (or when the chat was
    /// created / reopened). Drives the sidebar's "active chats, most recent first" ordering.</summary>
    public DateTimeOffset LastActivityUtc => _lastActivityUtc;

    /// <summary>True when this chat was loaded as read-only history — its bound worktree
    /// no longer exists, so sending and branch operations are disabled and nothing is
    /// re-saved.</summary>
    public bool IsReadOnly { get; private set; }

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

    /// <summary>One-shot composer pre-fill consumed by <c>ChatSessionPanel</c> when it binds
    /// this session. Lets a board step seed a prompt the user reviews before sending. Cleared
    /// by the panel once read.</summary>
    public string? DraftPrompt { get; set; }

    /// <summary>Paired with <see cref="DraftPrompt"/>: when true the panel sends the draft on bind
    /// instead of leaving it in the composer for review. Set by board-step launches; the clean hook
    /// a future "review before send" setting would flip off. Cleared by the panel once read.</summary>
    public bool AutoSendDraft { get; set; }

    /// <summary>Set when this session is bound to a git worktree. Drives the
    /// Merge / Delete branch toolbar buttons and the tile-header branch label.</summary>
    public WorktreeInfo? WorktreeInfo { get; private set; }

    /// <summary>Cached result of probing the workspace for a <c>.git</c> dir.
    /// The branch toolbar button uses this to gate enablement.</summary>
    public bool? IsGitRepository { get; set; }

    public IWorkspace Workspace => _workspace;

    internal void BindWorktree(WorktreeInfo info) => WorktreeInfo = info;

    /// <summary>User-supplied name set via <see cref="Rename"/>. When non-null it overrides
    /// the derived title everywhere the chat is labelled. Null means "use the derived title".</summary>
    public string? CustomTitle { get; private set; }

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomTitle)) return CustomTitle;
            var first = Turns.FirstOrDefault()?.UserMessage;
            if (string.IsNullOrWhiteSpace(first))
                return WorktreeInfo?.Branch ?? "New Chat";
            var trimmed = first.Trim().ReplaceLineEndings(" ");
            return trimmed.Length <= 32 ? trimmed : trimmed[..32] + "…";
        }
    }

    /// <summary>Set or clear the user's name for this chat. A whitespace-only value clears the
    /// override, reverting to the derived/slot title. Persists immediately (once the chat has
    /// content to save) so the rename survives reload and shows in History.</summary>
    public void Rename(string? title)
    {
        var trimmed = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (CustomTitle == trimmed) return;
        CustomTitle = trimmed;
        Raise();
        Persist();
    }

    public List<ChatTurn> Turns { get; } = new();
    public List<PendingApproval> Approvals { get; } = new();
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Transient "skip all permission prompts" toggle for this session. When on,
    /// the agent loop auto-accepts every approval prompt instead of asking the user. Tied to
    /// the live VM only — never persisted, so a rehydrated saved chat always starts with it off.</summary>
    public bool YoloMode { get; private set; }

    /// <summary>Flip YOLO mode. Resolving any already-queued approval prompts is left to the
    /// user — the toggle only changes how <em>future</em> prompts are handled.</summary>
    public void ToggleYoloMode()
    {
        YoloMode = !YoloMode;
        Raise();
    }

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

    /// <summary>Snapshot of the exact request that would be sent to the LLM on the next turn
    /// (messages, tools, raw JSON), for the "view raw context" diagnostic. Null before the first
    /// send, when no session/loop has been created yet.</summary>
    public ChatRequestPreview? GetContextPreview() => _loop?.BuildRequestPreview();

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

    public async Task SendAsync(string prompt, IReadOnlyList<ChatImage>? images = null)
    {
        if (IsRunning) return;
        if (IsReadOnly) return;
        if (string.IsNullOrWhiteSpace(prompt) && images is not { Count: > 0 }) return;

        EnsureStarted();

        var imageCount = images?.Count ?? 0;
        _diagnostics.Log(DiagnosticCategory.Session,
            $"user message sent ({prompt.Length} chars" + (imageCount > 0 ? $", {imageCount} image(s)" : "") + ")");

        var turn = new ChatTurn(prompt, images);
        Turns.Add(turn);

        await DriveAsync(turn, ct => _loop!.RunTurnAsync(prompt, images, ct)).ConfigureAwait(false);
    }

    /// <summary>Resume the most recent turn after it stopped at the tool-call iteration
    /// cap. No new user message is added — the agent loop picks up from the pending tool
    /// results. No-op unless that turn is actually parked at the cap and nothing is running.</summary>
    public async Task ContinueAsync()
    {
        if (IsRunning) return;
        if (IsReadOnly) return;
        if (_loop is null) return;

        var turn = Turns.LastOrDefault();
        if (turn is null || !turn.MaxIterationsReached) return;

        turn.MaxIterationsReached = false;
        _diagnostics.Log(DiagnosticCategory.Session, "continue (resume after iteration cap)");
        await DriveAsync(turn, ct => _loop.ContinueTurnAsync(ct)).ConfigureAwait(false);
    }

    /// <summary>Shared run scaffolding for both a fresh turn and a continuation: flips the
    /// running flags, streams events into <paramref name="turn"/>, then compacts and
    /// persists. The <paramref name="run"/> delegate is the only difference between the two.</summary>
    private async Task DriveAsync(ChatTurn turn, Func<CancellationToken, IAsyncEnumerable<ChatStreamEvent>> run)
    {
        turn.IsRunning = true;
        turn.Activity = TurnActivity.ProcessingPrompt;
        IsRunning = true;
        _lastActivityUtc = DateTimeOffset.UtcNow;
        Error = null;
        Raise();

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            await foreach (var ev in run(ct).ConfigureAwait(false))
            {
                Apply(turn, ev);
                Raise();
            }

            await MaybeCompactAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            turn.Error = "Cancelled.";
            _diagnostics.Log(DiagnosticCategory.Session, "cancelled");
        }
        catch (Exception ex)
        {
            turn.Error = ex.Message;
            Error = ex.Message;
            _diagnostics.Log(DiagnosticCategory.Error, $"exception: {ex.Message}");
        }
        finally
        {
            turn.IsRunning = false;
            turn.Activity = TurnActivity.Idle;
            IsRunning = false;
            _lastActivityUtc = DateTimeOffset.UtcNow;
            _runCts?.Dispose();
            _runCts = null;
            Raise();
            Persist();
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
        _diagnostics.Clear();
        _loop = null;     // forces a fresh ChatSession (system prompt re-rendered) on next send
        LockedEndpoint = null;  // a cleared chat is a fresh chat — re-pick endpoint on next send
        _lastReportedPromptTokens = null;
        _lastReportedCompletionTokens = null;
        Error = null;
        IsCompacting = false;
        CompactionError = null;
        CompactionBoundaryUiTurnIndex = null;
        LastCompactionSummary = null;

        // A cleared chat starts a new history record — rotate the id and timestamp, and
        // drop any not-yet-consumed restore state. The prior session's file is left
        // intact so it remains under History.
        PersistentId = Guid.NewGuid();
        _createdUtc = DateTimeOffset.UtcNow;
        _lastActivityUtc = _createdUtc;
        IsReadOnly = false;
        _restoredMessages = null;
        _restoredEndpoint = null;

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
            Persist();
        }
    }

    private void EnsureStarted()
    {
        if (_loop is not null) return;

        // Snapshot the chosen endpoint so later edits/deletes in Settings don't
        // disrupt this chat. Once locked, every subsequent turn uses the snapshot.
        // A restored chat re-locks the endpoint it was saved with (re-resolved from
        // settings by id so we recover the API key, which is never persisted).
        var endpoint = ResolveStartEndpoint();
        LockedEndpoint = endpoint;

        var completion = _clientFactory.CreateCompletionClient(endpoint);

        // Hand the subagent runner this chat's completion client so subagents launched from this
        // session inherit its endpoint/model by default (the workspace flows through ToolContext).
        _subagentRunner.Bind(completion);

        ChatSession session;
        if (_restoredMessages is { } restored)
        {
            // Resume: adopt the saved message log verbatim so the model sees the same
            // context (including any compaction summary). System prompt / instructions
            // are already baked into messages[0].
            session = ChatSession.Restore(restored);
            _restoredMessages = null;
            _restoredEndpoint = null;
        }
        else
        {
            var prompt = _settings.SystemPrompt;
            var hint = _settings.MarkdownEnabled
                ? SessionPrompts.MarkdownHint
                : SessionPrompts.PlainTextHint;
            prompt = (string.IsNullOrWhiteSpace(prompt) ? "" : prompt + "\n\n") + hint;
            var instructions = _instructionLoader.Load(_settings, _workspace).ToList();
            foreach (var tool in _tools.Tools)
            {
                var ctx = new ToolContext(_workspace, _permissions.RestrictToWorkspace(tool.Name));
                var contribution = tool.SessionStartMessage(ctx);
                if (!string.IsNullOrWhiteSpace(contribution))
                    instructions.Add(contribution);
            }
            session = new ChatSession(_workspace, prompt, instructions);
        }

        _loop = _loopFactory.Create(
            session, completion, _workspace,
            new AgentLoopOptions { MaxIterations = _settings.MaxToolIterations, OwnerId = Id.ToString() },
            isYoloEnabled: () => YoloMode,
            diagnostics: _diagnostics);

        StartApprovalConsumer();
        _ = DetectCapabilitiesAsync(endpoint);
    }

    private EndpointSettings ResolveStartEndpoint()
    {
        var endpoints = _settings.Endpoints;

        // Restored chat: prefer the saved endpoint by id (recovers the API key); if it
        // was deleted since, fall back to the saved non-secret snapshot with no key so
        // the user can re-pick before the next send.
        if (_restoredEndpoint is { } re)
            return endpoints.FindById(re.Id)
                   ?? new EndpointSettings(re.Id, re.Name, re.BaseUrl, ApiKey: "", re.Model);

        var resolved = SelectedEndpointId is Guid id ? endpoints.FindById(id) : null;
        return resolved ?? endpoints.Default;
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
        // Usage updates feed this view model's context-token bookkeeping; everything else is
        // shared turn-building logic (see ChatTurnApplier, reused by the subagent transcript).
        if (ev is UsageUpdateEvent usage)
        {
            _lastReportedPromptTokens = usage.PromptTokens;
            _lastReportedCompletionTokens = usage.CompletionTokens;
            return;
        }

        ChatTurnApplier.Apply(turn, ev);
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>Write the current state to disk. No-op for read-only history and for
    /// chats with nothing in them yet. Disk failures are swallowed — persistence must
    /// never break an active chat.</summary>
    private void Persist()
    {
        if (IsReadOnly) return;
        if (Turns.Count == 0) return;
        try { _store.Save(BuildPersistedChat()); }
        catch (Exception ex) { Console.Error.WriteLine($"yamca: failed to persist chat: {ex.Message}"); }
    }

    private PersistedChat BuildPersistedChat() => new()
    {
        Id = PersistentId,
        Title = Title,
        CustomTitle = CustomTitle,
        CreatedUtc = _createdUtc,
        Endpoint = LockedEndpoint is { } ep
            ? new PersistedEndpoint(ep.Id, ep.Name, ep.BaseUrl, ep.Model)
            : null,
        Worktree = WorktreeInfo,
        WorkspaceRootPath = _workspace.RootPath,
        Compaction = CompactionBoundaryUiTurnIndex is int b && LastCompactionSummary is { } s
            ? new PersistedCompaction(s, b)
            : null,
        Messages = _loop?.Session.Messages.ToList() ?? new List<ChatMessage>(),
        Turns = Turns.Select(MapTurn).ToList(),
    };

    private static PersistedTurn MapTurn(ChatTurn turn)
    {
        var pt = new PersistedTurn
        {
            UserMessage = turn.UserMessage,
            Error = turn.Error,
            Images = turn.Images.Count > 0 ? turn.Images.ToList() : null,
        };
        foreach (var item in turn.Items)
        {
            pt.Items.Add(item switch
            {
                AssistantTextItem a => new PersistedTurnItem { Kind = "text", Text = a.Text, IsComplete = a.IsComplete },
                ReasoningItem r => new PersistedTurnItem { Kind = "reasoning", Text = r.Text, IsComplete = r.IsComplete },
                ToolCallItem c => new PersistedTurnItem
                {
                    Kind = "tool",
                    CallId = c.CallId,
                    ToolName = c.ToolName,
                    ArgumentsJson = c.ArgumentsJson,
                    State = c.State.ToString(),
                    Result = c.Result,
                },
                _ => new PersistedTurnItem { Kind = "text", Text = "" },
            });
        }
        return pt;
    }

    /// <summary>Populate this VM from a saved session for display. When
    /// <paramref name="readOnly"/> is false, the message log and endpoint snapshot are
    /// stashed for <see cref="EnsureStarted"/> to resume from on the next send.</summary>
    internal void LoadFrom(PersistedChat doc, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(doc);

        PersistentId = doc.Id;
        CustomTitle = doc.CustomTitle;
        _createdUtc = doc.CreatedUtc;
        // A just-reopened chat is the most-recently-interacted-with — float it to the top
        // of the sidebar's active group.
        _lastActivityUtc = DateTimeOffset.UtcNow;
        IsReadOnly = readOnly;

        Turns.Clear();
        foreach (var pt in doc.Turns)
        {
            var turn = new ChatTurn(pt.UserMessage, pt.Images) { IsRunning = false, Error = pt.Error };
            foreach (var pi in pt.Items)
            {
                switch (pi.Kind)
                {
                    case "text":
                        var text = new AssistantTextItem { IsComplete = pi.IsComplete };
                        text.Append(pi.Text);
                        turn.AddItem(text);
                        break;
                    case "reasoning":
                        var reasoning = new ReasoningItem { IsComplete = pi.IsComplete };
                        reasoning.Append(pi.Text);
                        turn.AddItem(reasoning);
                        break;
                    case "tool":
                        turn.AddItem(new ToolCallItem
                        {
                            CallId = pi.CallId ?? "",
                            ToolName = pi.ToolName ?? "",
                            ArgumentsJson = pi.ArgumentsJson ?? "",
                            State = Enum.TryParse<ToolCallState>(pi.State, out var st) ? st : ToolCallState.Succeeded,
                            Result = pi.Result,
                        });
                        break;
                }
            }
            Turns.Add(turn);
        }

        if (doc.Worktree is { } wt) WorktreeInfo = wt;

        if (doc.Compaction is { } c)
        {
            CompactionBoundaryUiTurnIndex = c.BoundaryUiTurnIndex;
            LastCompactionSummary = c.Summary;
        }

        if (!readOnly)
        {
            _restoredMessages = doc.Messages;
            _restoredEndpoint = doc.Endpoint;
        }

        Raise();
    }

    public void Dispose()
    {
        try { _runCts?.Cancel(); } catch { /* ignore */ }
        try { _consumerCts?.Cancel(); } catch { /* ignore */ }
        _runCts?.Dispose();
        _consumerCts?.Dispose();
    }
}
