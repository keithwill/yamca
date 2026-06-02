namespace Yamca.Agent.Chat;

/// <summary>Bounded, in-memory diagnostic timeline for one chat session: model
/// round-trips (including the <c>finish_reason</c> that the higher-level
/// <see cref="ChatStreamEvent"/> stream drops), tool execution, and session
/// lifecycle events, intermixed in arrival order. Surfaced in the chat composer's
/// "Diagnostic Log" dialog. Bounded like <c>McpServerLogBuffer</c> so a long
/// session can't leak memory; the monotonic <see cref="DiagnosticEntry.Seq"/>
/// keeps ordering unambiguous even after old entries are evicted.</summary>
public sealed class SessionDiagnosticsLog
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<DiagnosticEntry> _entries = new();
    private readonly int _capacity;
    private int _seq;

    public SessionDiagnosticsLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Log(DiagnosticCategory category, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (_gate)
        {
            _entries.Enqueue(new DiagnosticEntry(++_seq, DateTimeOffset.UtcNow, category, message));
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate) { return _entries.ToArray(); }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); }
    }
}

public enum DiagnosticCategory
{
    /// <summary>Session lifecycle: a user turn started, continued, was cancelled.</summary>
    Session,

    /// <summary>A request was dispatched to the model endpoint.</summary>
    Request,

    /// <summary>Model-side stream milestones: tool-call generation started, assistant
    /// turn completed (with its <c>finish_reason</c>).</summary>
    Model,

    /// <summary>Server-reported token usage.</summary>
    Usage,

    /// <summary>Tool invocation and results.</summary>
    Tool,

    /// <summary>An error surfaced (failed request, exception, denied call).</summary>
    Error,
}

public sealed record DiagnosticEntry(
    int Seq,
    DateTimeOffset Timestamp,
    DiagnosticCategory Category,
    string Message);
