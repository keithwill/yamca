using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Web.Services;

/// <summary>A live "Ask" prompt waiting for the user. The Chat page renders one of
/// these as an inline card; clicking Allow/Deny resolves the underlying
/// <see cref="ApprovalRequest"/> the agent loop is awaiting.</summary>
public sealed class PendingApproval
{
    public PendingApproval(ApprovalRequest request)
    {
        Request = request;
    }

    public ApprovalRequest Request { get; }
    public string ToolName => Request.ToolName;

    public string ArgumentsPretty => JsonSerializer.Serialize(
        Request.Arguments,
        new JsonSerializerOptions { WriteIndented = true });
}
