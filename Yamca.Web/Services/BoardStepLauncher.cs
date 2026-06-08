using Yamca.Agent.Board;
using Yamca.Agent.Git;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Web.Services;

/// <summary>Inputs for launching a chat session to work a single board step.</summary>
public sealed record StepRunRequest(
    BoardCard Card,
    BoardColumn CurrentColumn,
    BoardColumn? NextColumn,
    Guid EndpointId,
    WorktreeInfo? Worktree,
    string? ColumnInstructions);

/// <summary>Provisions a chat session pre-seeded to work a board step: bound to the card's
/// branch worktree (if any), with a self-contained draft prompt (card + the column's instructions.md
/// inlined) pre-filled in the composer. The session is flagged to auto-send the draft on open via
/// <see cref="ChatViewModel.AutoSendDraft"/>.</summary>
public sealed class BoardStepLauncher
{
    private readonly ChatSessionManager _sessions;

    public BoardStepLauncher(ChatSessionManager sessions) => _sessions = sessions;

    /// <summary>Create the session for a step, shown automatically in a split pane.</summary>
    public ChatViewModel ProvisionStepSession(StepRunRequest request)
    {
        var vm = request.Worktree is { } wt
            ? _sessions.CreateForWorktree(new WorkspaceImpl(wt.WorktreePath), wt)
            : _sessions.Create();

        vm.SelectEndpoint(request.EndpointId);
        vm.DraftPrompt = BoardPrompts.BuildSeedPrompt(request.Card, request.CurrentColumn, request.ColumnInstructions);
        vm.AutoSendDraft = true;
        return vm;
    }
}
