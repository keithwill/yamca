using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
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

    private AgentLoop? _loop;
    private CancellationTokenSource? _runCts;
    private Task? _approvalConsumer;
    private CancellationTokenSource? _consumerCts;

    public ChatViewModel(
        IWorkspace workspace,
        IToolRegistry tools,
        IPermissionResolver permissions,
        IApprovalCoordinator approvals,
        IPermissionStore permissionStore,
        SessionSettings settings,
        InstructionFilesLoader instructionLoader)
    {
        _workspace = workspace;
        _tools = tools;
        _permissions = permissions;
        _approvals = approvals;
        _permissionStore = permissionStore;
        _settings = settings;
        _instructionLoader = instructionLoader;
    }

    public List<ChatTurn> Turns { get; } = new();
    public List<PendingApproval> Approvals { get; } = new();
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Char/4 estimate of input tokens currently in the conversation.
    /// Zero before the first message. (Server-reported usage is unavailable until
    /// the OpenAI .NET SDK exposes stream_options.include_usage publicly.)</summary>
    public int CurrentContextTokens => _loop?.Session.EstimatedInputTokens ?? 0;

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

    public void Clear()
    {
        if (IsRunning) Cancel();
        Turns.Clear();
        Approvals.Clear();
        _loop = null;     // forces a fresh ChatSession (system prompt re-rendered) on next send
        Error = null;
        Raise();
    }

    private void EnsureStarted()
    {
        if (_loop is not null) return;

        var endpoint = _settings.Endpoint;
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "sk-local" : endpoint.ApiKey);
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
        var modelId = string.IsNullOrWhiteSpace(endpoint.Model) ? "local-model" : endpoint.Model;
        var chatClient = new ChatClient(modelId, credential, clientOptions);
        var completion = new OpenAIChatCompletionClient(chatClient);

        var prompt = _settings.SystemPrompt;
        var hint = _settings.MarkdownEnabled
            ? "Your responses are rendered as GitHub-flavored Markdown — use fenced code blocks for code, and standard Markdown for emphasis, lists, and tables."
            : "Your responses are rendered as plain text. Do NOT use Markdown formatting: no `backticks`, no **bold**/*italics*, no #headings, no fenced code blocks, no bullet/numbered lists. Write code and identifiers inline as plain text.";
        prompt = (string.IsNullOrWhiteSpace(prompt) ? "" : prompt + "\n\n") + hint;
        var instructions = _instructionLoader.Load(_settings, _workspace);
        var session = new ChatSession(_workspace, prompt, instructions);

        _loop = new AgentLoop(
            session, completion, _tools, _permissions, _approvals, _permissionStore, _workspace);

        StartApprovalConsumer();
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
