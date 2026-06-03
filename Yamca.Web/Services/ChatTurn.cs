using System.Text;
using Yamca.Agent.Chat;

namespace Yamca.Web.Services;

public sealed class ChatTurn
{
    public ChatTurn(string userMessage, IReadOnlyList<ChatImage>? images = null)
    {
        UserMessage = userMessage;
        Images = images ?? Array.Empty<ChatImage>();
    }

    public string UserMessage { get; }

    /// <summary>Images the user attached to this turn, rendered as thumbnails in the
    /// user bubble. Empty for turns without image attachments.</summary>
    public IReadOnlyList<ChatImage> Images { get; }

    // The item list is written by the agent loop on a background continuation
    // (Apply runs after `await ... ConfigureAwait(false)`) while the Blazor renderer
    // enumerates it on the dispatcher. Without this gate, a streamed Add racing the
    // render throws "Collection was modified". Reads return a snapshot so the renderer
    // never enumerates the live list. Mirrors the per-item _gate on the text/reasoning
    // buffers below.
    private readonly List<ChatTurnItem> _items = new();
    private readonly object _itemsGate = new();

    public IReadOnlyList<ChatTurnItem> Items
    {
        get { lock (_itemsGate) return _items.ToArray(); }
    }

    internal void AddItem(ChatTurnItem item)
    {
        lock (_itemsGate) _items.Add(item);
    }

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
