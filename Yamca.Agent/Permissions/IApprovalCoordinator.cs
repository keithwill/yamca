using System.Text.Json;
using System.Threading.Channels;

namespace Yamca.Agent.Permissions;

public interface IApprovalCoordinator
{
    /// <summary>Stream of outstanding approval prompts for the UI to render.</summary>
    ChannelReader<ApprovalRequest> Pending { get; }

    /// <summary>
    /// Enqueue an approval prompt and await the UI's decision. Cancellation
    /// abandons the request without auto-denying.
    /// </summary>
    Task<ApprovalDecision> RequestApprovalAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
