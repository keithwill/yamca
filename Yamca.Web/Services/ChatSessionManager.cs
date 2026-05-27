using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Git;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Per-circuit owner of the user's open chat sessions. Caps the number
/// of concurrent sessions so the future tiled view stays a sensible 1–4 grid.</summary>
public sealed class ChatSessionManager : IDisposable
{
    public const int MaxSessions = 4;

    private readonly IServiceProvider _services;
    private readonly List<ChatViewModel> _sessions = new();
    private int _nextId = 1;

    public ChatSessionManager(IServiceProvider services)
    {
        _services = services;
    }

    public IReadOnlyList<ChatViewModel> Sessions => _sessions;

    public bool CanCreate => _sessions.Count < MaxSessions;

    public event Action? Changed;

    public ChatViewModel Create()
    {
        if (!CanCreate) throw new InvalidOperationException($"Maximum of {MaxSessions} chat sessions reached.");
        var id = _nextId++;
        var vm = ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id);
        vm.Changed += Raise;
        _sessions.Add(vm);
        Raise();
        return vm;
    }

    /// <summary>Create a session bound to a git worktree. The supplied
    /// <paramref name="workspace"/> replaces the DI-resolved root workspace so
    /// every tool call inside the session resolves paths against the worktree.</summary>
    public ChatViewModel CreateForWorktree(IWorkspace workspace, WorktreeInfo info)
    {
        if (!CanCreate) throw new InvalidOperationException($"Maximum of {MaxSessions} chat sessions reached.");
        var id = _nextId++;
        var vm = ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id, workspace);
        vm.BindWorktree(info);
        vm.IsGitRepository = true;
        vm.Changed += Raise;
        _sessions.Add(vm);
        Raise();
        return vm;
    }

    public void Close(int id)
    {
        var vm = _sessions.FirstOrDefault(s => s.Id == id);
        if (vm is null) return;
        vm.Changed -= Raise;
        _sessions.Remove(vm);
        vm.Dispose();
        Raise();
    }

    public ChatViewModel? Get(int id) => _sessions.FirstOrDefault(s => s.Id == id);

    /// <summary>Returns the id of the session that should become active after
    /// closing <paramref name="closingId"/>: the next session in the list, or
    /// the previous if it was last. Null when nothing remains.</summary>
    public int? PickNextActive(int closingId)
    {
        var idx = _sessions.FindIndex(s => s.Id == closingId);
        if (idx < 0) return _sessions.FirstOrDefault()?.Id;
        if (idx + 1 < _sessions.Count) return _sessions[idx + 1].Id;
        if (idx - 1 >= 0) return _sessions[idx - 1].Id;
        return null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        foreach (var vm in _sessions)
        {
            vm.Changed -= Raise;
            vm.Dispose();
        }
        _sessions.Clear();
    }
}
