using System.Collections.Concurrent;

namespace Yamca.Agent.Mcp;

/// <summary>
/// Bounded ring buffer of stderr/diagnostic lines for one MCP server, surfaced
/// in the settings UI. stdio servers chatter freely on stderr; an unbounded
/// buffer would leak memory over a long session.
/// </summary>
public sealed class McpServerLogBuffer
{
    public const int DefaultCapacity = 200;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;

    public McpServerLogBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Append(string source, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var entry = new LogEntry(DateTimeOffset.UtcNow, source, line);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) { return _entries.ToArray(); }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); }
    }

    public sealed record LogEntry(DateTimeOffset Timestamp, string Source, string Line);
}
