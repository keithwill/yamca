using System.Diagnostics;
using System.Text;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Lifecycle state of a background process. <see cref="Failed"/> means it failed to start
/// or exited with a non-zero code; <see cref="Exited"/> means a clean (zero) exit.</summary>
public enum ProcessStatus { Running, Exited, Failed }

/// <summary>What the caller asked <see cref="IBackgroundProcessManager.Start"/> to run. Retained on
/// the handle so <c>restart</c> can re-launch with the identical command, shell, and working dir.</summary>
public sealed record StartRequest(
    string Name,
    string Command,
    string WorkingDirectory,
    string? StopCommand,
    IReadOnlyList<int> Ports,
    ShellPreference ShellPreference);

/// <summary>One captured output line, tagged with the stream it came from and a monotonic sequence
/// number so callers can resume from a cursor.</summary>
public sealed record OutputLine(long Seq, string Text, bool IsError);

/// <summary>A point-in-time read of a process's captured output: the lines after the requested
/// cursor that are still buffered, the cursor to pass next time, and whether earlier lines were
/// dropped from the ring buffer.</summary>
public sealed record OutputSnapshot(IReadOnlyList<OutputLine> Lines, long NextCursor, bool Truncated);

/// <summary>
/// A single long-lived child process owned by <see cref="BackgroundProcessManager"/>. The handle is
/// the UI-facing snapshot (status fields have internal setters so only the manager mutates them) and
/// also owns a bounded, thread-safe ring buffer of the process's combined stdout/stderr. Output
/// callbacks fire on background threads, so every accessor is guarded by <see cref="_gate"/>.
/// </summary>
public sealed class BackgroundProcess
{
    // Keep memory bounded: a chatty server shouldn't grow without limit. We keep the last N lines
    // (failures and recent activity cluster at the tail) and cap any single pathological line.
    private const int MaxLines = 2000;
    private const int MaxLineChars = 16_000;

    private readonly object _gate = new();
    private readonly Queue<OutputLine> _ring = new();
    private long _seq;          // total lines ever appended (also the highest sequence number)
    private bool _dropped;      // true once the ring has evicted at least one line

    public BackgroundProcess(StartRequest request, ShellKind shellKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        ShellKind = shellKind;
        StartedAt = DateTimeOffset.Now;
    }

    internal StartRequest Request { get; }

    public string Name => Request.Name;
    public string Command => Request.Command;
    public string WorkingDirectory => Request.WorkingDirectory;
    public string? StopCommand => Request.StopCommand;
    public IReadOnlyList<int> Ports => Request.Ports;
    public ShellKind ShellKind { get; }
    public DateTimeOffset StartedAt { get; }

    public int? Pid { get; private set; }
    public ProcessStatus Status { get; private set; } = ProcessStatus.Running;
    public int? ExitCode { get; private set; }
    public DateTimeOffset? ExitedAt { get; private set; }

    /// <summary>The OS handle, held so the manager can wait on and kill the tree. Internal: the UI
    /// (a separate assembly) only ever sees the public snapshot fields.</summary>
    internal Process? Underlying { get; private set; }

    internal void Bind(Process process, int pid)
    {
        Underlying = process;
        Pid = pid;
    }

    internal void Append(string text, bool isError)
    {
        lock (_gate)
        {
            var capped = text.Length > MaxLineChars ? text[..MaxLineChars] + "…[truncated]" : text;
            _ring.Enqueue(new OutputLine(++_seq, capped, isError));
            while (_ring.Count > MaxLines)
            {
                _ring.Dequeue();
                _dropped = true;
            }
        }
    }

    internal void MarkExited(int exitCode)
    {
        lock (_gate)
        {
            if (Status != ProcessStatus.Running) return;
            ExitCode = exitCode;
            Status = exitCode == 0 ? ProcessStatus.Exited : ProcessStatus.Failed;
            ExitedAt = DateTimeOffset.Now;
        }
    }

    internal void MarkStartFailed(string message)
    {
        lock (_gate)
        {
            Status = ProcessStatus.Failed;
            ExitedAt = DateTimeOffset.Now;
            _ring.Enqueue(new OutputLine(++_seq, $"failed to start: {message}", IsError: true));
        }
    }

    /// <summary>Read buffered output. With <paramref name="sinceSeq"/> null, returns the whole
    /// retained tail; otherwise only lines whose sequence is greater than the cursor.</summary>
    public OutputSnapshot ReadOutput(long? sinceSeq)
    {
        lock (_gate)
        {
            var lines = sinceSeq is long cursor
                ? _ring.Where(l => l.Seq > cursor).ToArray()
                : _ring.ToArray();
            // Truncated is only meaningful for a full read; an incremental caller that has kept up
            // never sees a gap.
            var truncated = _dropped && sinceSeq is null;
            return new OutputSnapshot(lines, _seq, truncated);
        }
    }

    /// <summary>Render the retained tail as plain text (used by the UI output view and the
    /// <c>get_process_output</c> tool summary).</summary>
    public string RenderTail()
    {
        var snapshot = ReadOutput(sinceSeq: null);
        var sb = new StringBuilder();
        if (snapshot.Truncated) sb.Append("…[earlier output truncated]\n");
        foreach (var line in snapshot.Lines) sb.Append(line.Text).Append('\n');
        return sb.ToString();
    }
}
