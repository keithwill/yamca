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

    public bool IsDiscoveredScript => string.Equals(ToolName, "execute_discovered_script", StringComparison.Ordinal);

    /// <summary>The <c>script_path</c> argument, when this is a script approval.</summary>
    public string? ScriptPath =>
        Request.Arguments.ValueKind == JsonValueKind.Object
        && Request.Arguments.TryGetProperty("script_path", out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    /// <summary>The <c>arguments</c> array as a typed list, or null if absent / malformed.</summary>
    public IReadOnlyList<string>? ScriptArguments
    {
        get
        {
            if (Request.Arguments.ValueKind != JsonValueKind.Object) return null;
            if (!Request.Arguments.TryGetProperty("arguments", out var prop) || prop.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>(prop.GetArrayLength());
            foreach (var el in prop.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) return null;
                list.Add(el.GetString() ?? "");
            }
            return list;
        }
    }
}
