using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Chat.Persistence;
using Yamca.Agent.Git;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Per-circuit owner of the user's open chat sessions. The set of loaded
/// ("active") sessions is uncapped; a separate visible set (<see cref="MaxPanes"/>)
/// tracks which of them are shown as panes in the split grid.</summary>
public sealed class ChatSessionManager : IDisposable
{
    /// <summary>Maximum number of chats shown at once in the split grid. The grid CSS
    /// (<c>yamca-split-1..4</c>) is laid out for 1–4 panes.</summary>
    public const int MaxPanes = 4;

    private readonly IServiceProvider _services;
    private readonly List<ChatViewModel> _sessions = new();
    private readonly List<int> _visible = new();
    private int _nextId = 1;

    public ChatSessionManager(IServiceProvider services)
    {
        _services = services;
    }

    public IReadOnlyList<ChatViewModel> Sessions => _sessions;

    /// <summary>The sessions currently shown as panes, in pane order. Skips any id that
    /// is no longer a live session (defensive — <see cref="Close"/> keeps these in sync).</summary>
    public IReadOnlyList<ChatViewModel> Visible =>
        _visible.Select(Get).Where(s => s is not null).Select(s => s!).ToList();

    public bool IsVisible(int id) => _visible.Contains(id);

    /// <summary>Place a session into a pane. No-op if already visible. When the grid is
    /// full the last pane is replaced — the displaced chat stays active, just hidden.</summary>
    public void Show(int id)
    {
        if (_visible.Contains(id)) return;
        if (_visible.Count < MaxPanes) _visible.Add(id);
        else _visible[^1] = id;
        Raise();
    }

    /// <summary>Remove a session from the grid without unloading it — it stays active.</summary>
    public void Hide(int id)
    {
        if (_visible.Remove(id)) Raise();
    }

    public event Action? Changed;

    public ChatViewModel Create()
    {
        var id = _nextId++;
        var vm = ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id);
        vm.Changed += Raise;
        _sessions.Add(vm);
        Show(id);
        Raise();
        return vm;
    }

    /// <summary>Create a session bound to a git worktree. The supplied
    /// <paramref name="workspace"/> replaces the DI-resolved root workspace so
    /// every tool call inside the session resolves paths against the worktree.</summary>
    public ChatViewModel CreateForWorktree(IWorkspace workspace, WorktreeInfo info)
    {
        var id = _nextId++;
        var vm = ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id, workspace);
        vm.BindWorktree(info);
        vm.IsGitRepository = true;
        vm.Changed += Raise;
        _sessions.Add(vm);
        Show(id);
        Raise();
        return vm;
    }

    /// <summary>Reopen a saved chat into a free slot. <paramref name="sessionWorkspace"/>
    /// replaces the DI-resolved root workspace when the chat is bound to a live worktree;
    /// pass <c>null</c> to use the root workspace (for non-worktree chats and read-only
    /// history of a worktree that no longer exists).</summary>
    public ChatViewModel Rehydrate(PersistedChat doc, IWorkspace? sessionWorkspace, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var id = _nextId++;
        var vm = sessionWorkspace is null
            ? ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id)
            : ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id, sessionWorkspace);
        vm.LoadFrom(doc, readOnly);
        if (doc.Worktree is not null && !readOnly) vm.IsGitRepository = true;
        vm.Changed += Raise;
        _sessions.Add(vm);
        Show(id);
        Raise();
        return vm;
    }

    /// <summary>The open session restored from the given saved chat, if any. Used to
    /// avoid loading a duplicate when the user reopens a chat that's already in a slot.</summary>
    public ChatViewModel? FindByPersistentId(Guid persistentId) =>
        _sessions.FirstOrDefault(s => s.PersistentId == persistentId);

    /// <summary>Fully unload a session: remove it from the grid and from the active set,
    /// then dispose it. The persisted chat on disk is left intact (reachable via History).</summary>
    public void Close(int id)
    {
        var vm = _sessions.FirstOrDefault(s => s.Id == id);
        if (vm is null) return;
        _visible.Remove(id);
        vm.Changed -= Raise;
        _sessions.Remove(vm);
        vm.Dispose();
        Raise();
    }

    public ChatViewModel? Get(int id) => _sessions.FirstOrDefault(s => s.Id == id);

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
