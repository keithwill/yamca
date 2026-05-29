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
/// branch worktree (if any), with the column's instructions.md injected as a system seed and a
/// draft prompt pre-filled in the composer for the user to review. The session is NOT sent —
/// the pure builders in <see cref="BoardPrompts"/> leave a clean path to auto-send for automation.</summary>
public sealed class BoardStepLauncher
{
    private readonly ChatSessionManager _sessions;

    public BoardStepLauncher(ChatSessionManager sessions) => _sessions = sessions;

    /// <summary>True when a chat slot is free to host a new step session.</summary>
    public bool HasFreeSlot => _sessions.CanCreate;

    /// <summary>Create the session for a step. Throws <see cref="InvalidOperationException"/>
    /// when all session slots are occupied (callers should guard with <see cref="HasFreeSlot"/>).</summary>
    public ChatViewModel ProvisionStepSession(StepRunRequest request)
    {
        var vm = request.Worktree is { } wt
            ? _sessions.CreateForWorktree(new WorkspaceImpl(wt.WorktreePath), wt)
            : _sessions.Create();

        vm.SelectEndpoint(request.EndpointId);
        if (!string.IsNullOrWhiteSpace(request.ColumnInstructions))
            vm.AddSeedInstruction(BoardPrompts.BuildStepInstruction(request.CurrentColumn, request.ColumnInstructions));
        vm.DraftPrompt = BoardPrompts.BuildSeedPrompt(request.Card, request.CurrentColumn, request.NextColumn);
        return vm;
    }
}
