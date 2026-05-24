using System.Text.Json;
using System.Threading.Channels;

namespace Yamca.Agent.Permissions;

/// <summary>
/// Per-circuit coordinator that pairs runtime approval prompts with UI decisions.
/// Persistence intent is returned to the caller via <see cref="ApprovalDecision.Persistence"/>;
/// actually writing the persisted choice back to localStorage is the UI layer's job.
/// </summary>
public sealed class ApprovalCoordinator : IApprovalCoordinator
{
    private readonly Channel<ApprovalRequest> _channel = Channel.CreateUnbounded<ApprovalRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<ApprovalRequest> Pending => _channel.Reader;

    public async Task<ApprovalDecision> RequestApprovalAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var request = new ApprovalRequest(toolName, arguments);
        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        await using var reg = cancellationToken.Register(() => request.Cancel(cancellationToken));
        return await request.Completion.ConfigureAwait(false);
    }
}
