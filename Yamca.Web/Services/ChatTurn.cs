using System.Text;

namespace Yamca.Web.Services;

public sealed class ChatTurn
{
    public ChatTurn(string userMessage)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
    public List<ChatTurnItem> Items { get; } = new();
    public bool IsRunning { get; internal set; } = true;
    public string? Error { get; internal set; }

    /// <summary>Set when the agent loop stopped because it hit the configured tool-call
    /// iteration cap rather than finishing with a plain reply. Drives the "continue"
    /// affordance in the turn view; cleared when the user resumes the turn.</summary>
    public bool MaxIterationsReached { get; internal set; }

    /// <summary>What the agent is currently waiting on, used to drive the "Thinking"
    /// status row. Updated as stream events arrive; reset to <see cref="TurnActivity.Idle"/>
    /// while tokens stream (the content itself is the indicator) and when the turn ends.</summary>
    public TurnActivity Activity { get; internal set; } = TurnActivity.Idle;
}

/// <summary>Coarse "what are we waiting for right now" state for an in-flight turn.
/// Only meaningful while <see cref="ChatTurn.IsRunning"/> is true.</summary>
public enum TurnActivity
{
    /// <summary>Not waiting on anything visible — either tokens are actively streaming
    /// (the content is its own indicator) or the turn has finished.</summary>
    Idle,

    /// <summary>A request was sent and we're waiting for the model to process the prompt
    /// (user message or tool results) before the first token streams back. Shown with the
    /// lightbulb icon.</summary>
    ProcessingPrompt,

    /// <summary>One or more tool calls are executing; we're waiting for their results.
    /// Shown with the wrench/tools icon.</summary>
    RunningTools,
}

public abstract class ChatTurnItem;

public sealed class AssistantTextItem : ChatTurnItem
{
    // StringBuilder is not thread-safe. The agent loop appends tokens from a
    // background continuation while the Blazor renderer reads Text from the
    // dispatcher — without this gate, ToString() can observe a half-resized
    // chunk list and throw ArgumentOutOfRangeException.
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public bool IsComplete { get; internal set; }

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate) _buffer.Append(value);
    }

    public string Text
    {
        get { lock (_gate) return _buffer.ToString(); }
    }
}

public sealed class ReasoningItem : ChatTurnItem
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public bool IsComplete { get; internal set; }

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate) _buffer.Append(value);
    }

    public string Text
    {
        get { lock (_gate) return _buffer.ToString(); }
    }
}

public enum ToolCallState
{
    Pending,    // approved (or didn't need approval), about to run
    Succeeded,
    Failed,
    Denied,
}

public sealed class ToolCallItem : ChatTurnItem
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public ToolCallState State { get; internal set; } = ToolCallState.Pending;
    public string? Result { get; internal set; }
}
