using Yamca.Agent.Chat;
using Yamca.Agent.Subagents;

namespace Yamca.Web.Services;

/// <summary>Per-circuit store of subagent runs, populated by the runner via
/// <see cref="ISubagentObserver"/> and read by the UI (the <c>subagent_run</c> card button and
/// the composer indicator) to show a live, read-only transcript. Runs are retained for the
/// circuit's lifetime so the parent session stays diagnosable; <see cref="Clear"/> empties them
/// on demand.
///
/// Observer callbacks arrive on a background continuation (off the Blazor dispatcher), so all
/// access is guarded and the agent state is applied to the thread-safe <see cref="ChatTurn"/>.
/// Subscribers to <see cref="Changed"/> must marshal to the dispatcher (InvokeAsync) themselves.</summary>
public sealed class SubagentSessionRegistry : ISubagentObserver
{
    private readonly object _gate = new();
    private readonly List<SubagentLiveSession> _sessions = new();
    private readonly Dictionary<string, SubagentLiveSession> _byRunId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentLiveSession> _byCallId = new(StringComparer.Ordinal);

    /// <summary>Raised after any change (started / event / completed / cleared).</summary>
    public event Action? Changed;

    /// <summary>All runs, oldest first.</summary>
    public IReadOnlyList<SubagentLiveSession> Sessions
    {
        get { lock (_gate) return _sessions.ToArray(); }
    }

    /// <summary>Runs launched by a given chat session, oldest first.</summary>
    public IReadOnlyList<SubagentLiveSession> SessionsFor(string ownerId)
    {
        lock (_gate) return _sessions.Where(s => s.OwnerId == ownerId).ToArray();
    }

    /// <summary>The run launched by the given parent tool-call id, if any.</summary>
    public SubagentLiveSession? ByCallId(string callId)
    {
        lock (_gate) return _byCallId.GetValueOrDefault(callId);
    }

    /// <summary>The child runs of a given loop, oldest first — lets the UI group a batch
    /// <c>loop</c>'s items under one parent.</summary>
    public IReadOnlyList<SubagentLiveSession> ByLoopId(string loopRunId)
    {
        lock (_gate) return _sessions.Where(s => s.LoopRunId == loopRunId).ToArray();
    }

    /// <summary>How many of a chat session's runs are still in progress (drives the toolbar badge).</summary>
    public int RunningCountFor(string ownerId)
    {
        lock (_gate) return _sessions.Count(s => s.OwnerId == ownerId && s.Status == SubagentRunStatus.Running);
    }

    /// <summary>Whether a chat session has launched any runs (drives the toolbar button's visibility).</summary>
    public bool HasAnyFor(string ownerId)
    {
        lock (_gate) return _sessions.Any(s => s.OwnerId == ownerId);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _byRunId.Clear();
            _byCallId.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>Drop only the runs launched by the given chat session, leaving other
    /// sessions' runs intact (the registry is shared across the circuit's chats).</summary>
    public void ClearFor(string ownerId)
    {
        lock (_gate)
        {
            var drop = _sessions.Where(s => s.OwnerId == ownerId).ToList();
            foreach (var s in drop)
            {
                _sessions.Remove(s);
                _byRunId.Remove(s.RunId);
                if (!string.IsNullOrEmpty(s.ParentCallId))
                    _byCallId.Remove(s.ParentCallId);
            }
        }
        Changed?.Invoke();
    }

    public void OnStarted(SubagentRunInfo info)
    {
        var session = new SubagentLiveSession(info);
        lock (_gate)
        {
            _sessions.Add(session);
            _byRunId[info.RunId] = session;
            if (!string.IsNullOrEmpty(info.ParentCallId))
                _byCallId[info.ParentCallId] = session;
        }
        Changed?.Invoke();
    }

    public void OnEvent(string runId, ChatStreamEvent ev)
    {
        SubagentLiveSession? session;
        lock (_gate) session = _byRunId.GetValueOrDefault(runId);
        if (session is null) return;

        // ChatTurn is internally thread-safe; no need to hold _gate while applying.
        ChatTurnApplier.Apply(session.Turn, ev);
        Changed?.Invoke();
    }

    public void OnCompleted(string runId, bool isError, string result)
    {
        SubagentLiveSession? session;
        lock (_gate) session = _byRunId.GetValueOrDefault(runId);
        if (session is null) return;

        session.Status = isError ? SubagentRunStatus.Failed : SubagentRunStatus.Succeeded;
        session.Result = result;
        session.CompletedAt = DateTimeOffset.Now;
        session.Turn.IsRunning = false;
        session.Turn.Activity = TurnActivity.Idle;
        Changed?.Invoke();
    }
}
