using System.Text.Json;

namespace Yamca.Agent.Permissions;

/// <summary>
/// An outstanding "Ask" prompt awaiting a UI decision. Constructed by
/// <see cref="ApprovalCoordinator"/>; resolved by the UI calling
/// <see cref="Approve"/> or <see cref="Deny"/>.
/// </summary>
public sealed class ApprovalRequest
{
    private readonly TaskCompletionSource<ApprovalDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalRequest(string toolName, JsonElement arguments)
    {
        ToolName = toolName;
        Arguments = arguments;
    }

    public string ToolName { get; }
    public JsonElement Arguments { get; }

    internal Task<ApprovalDecision> Completion => _completion.Task;

    public bool Approve(ApprovalPersistence persistence = ApprovalPersistence.None) =>
        _completion.TrySetResult(new ApprovalDecision(true, persistence));

    public bool Deny(ApprovalPersistence persistence = ApprovalPersistence.None) =>
        _completion.TrySetResult(new ApprovalDecision(false, persistence));

    internal bool Cancel(CancellationToken ct) =>
        _completion.TrySetCanceled(ct);
}
