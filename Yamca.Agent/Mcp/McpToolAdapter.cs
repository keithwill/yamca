using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Mcp;

/// <summary>
/// Bridges one <see cref="McpClientTool"/> into Yamca's <see cref="ITool"/>
/// surface. The display/registry name is namespaced — <c>mcp__&lt;serverId&gt;__&lt;tool&gt;</c> —
/// so two servers can expose tools with the same underlying name without
/// colliding in the registry.
/// </summary>
public sealed class McpToolAdapter : ITool
{
    public const string NamePrefix = "mcp__";

    private readonly McpClientTool _tool;
    private readonly McpServerLogBuffer _log;
    private readonly TimeSpan _callTimeout;
    private string? _schemaJsonCache;

    public McpToolAdapter(string serverId, McpClientTool tool, McpServerLogBuffer log, TimeSpan callTimeout)
    {
        if (string.IsNullOrEmpty(serverId)) throw new ArgumentException("Server id is required.", nameof(serverId));
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(log);

        ServerId = serverId;
        _tool = tool;
        _log = log;
        _callTimeout = callTimeout > TimeSpan.Zero ? callTimeout : McpServerConnection.DefaultCallTimeout;

        UnderlyingToolName = tool.Name;
        Name = BuildName(serverId, tool.Name);
        Description = BuildDescription(serverId, tool);
    }

    public string ServerId { get; }
    public string UnderlyingToolName { get; }
    public string Name { get; }
    public string Description { get; }

    public string ParametersSchema => _schemaJsonCache ??= _tool.JsonSchema.GetRawText();

    // MCP servers manage their own scope — workspace confinement only makes
    // sense for in-process filesystem tools.
    public bool SupportsWorkspaceRestriction => false;

    // Third-party code: opt-in by default.
    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    // Deferred so a user with several busy servers doesn't pay the token cost
    // of dozens of tool schemas on every iteration.
    public bool Deferred => true;

    public bool ExposedInSettings => true;

    public static string BuildName(string serverId, string toolName) =>
        $"{NamePrefix}{serverId}__{toolName}";

    /// <summary>True if <paramref name="toolName"/> looks like an MCP-prefixed
    /// adapter name. Used by the approval prompt to render the <c>[mcp: id]</c>
    /// label without having to look up the tool.</summary>
    public static bool TryParseName(string toolName, out string serverId, out string underlyingTool)
    {
        serverId = string.Empty;
        underlyingTool = string.Empty;
        if (string.IsNullOrEmpty(toolName) || !toolName.StartsWith(NamePrefix, StringComparison.Ordinal))
            return false;

        var rest = toolName.AsSpan(NamePrefix.Length);
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0 || sep >= rest.Length - 2) return false;

        serverId = rest.Slice(0, sep).ToString();
        underlyingTool = rest.Slice(sep + 2).ToString();
        return true;
    }

    private static string BuildDescription(string serverId, McpClientTool tool)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append("mcp: ").Append(serverId).Append("] ");
        var serverDescription = tool.Description ?? string.Empty;
        if (serverDescription.Length == 0)
            sb.Append("(no description provided by the MCP server).");
        else
            sb.Append(serverDescription);
        return sb.ToString();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?>? args = null;
        try
        {
            args = ConvertArguments(arguments);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Invalid arguments for MCP tool '{Name}': {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_callTimeout);

        CallToolResult result;
        try
        {
            result = await _tool.CallAsync(args, progress: null, options: null, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var msg = $"MCP tool '{Name}' did not respond within {_callTimeout.TotalSeconds:F0}s.";
            _log.Append("yamca", msg);
            return ToolResult.Error(msg);
        }
        catch (Exception ex)
        {
            _log.Append("yamca", $"call '{UnderlyingToolName}' threw: {ex.Message}");
            return ToolResult.Error($"MCP tool '{Name}' failed: {ex.Message}");
        }

        return ToToolResult(result);
    }

    private static IReadOnlyDictionary<string, object?>? ConvertArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null)
            return null;
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Expected a JSON object, got {arguments.ValueKind}.");

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in arguments.EnumerateObject())
        {
            // The MCP SDK serializes arguments using its own JsonSerializerOptions.
            // Passing the raw JsonElement keeps fidelity without requiring us to
            // know the target schema.
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }

    public static ToolResult ToToolResult(CallToolResult result)
    {
        var sb = new StringBuilder();

        if (result.Content is { Count: > 0 })
        {
            foreach (var block in result.Content)
            {
                AppendBlock(sb, block);
            }
        }
        else if (result.StructuredContent is { } structured)
        {
            sb.Append(structured.GetRawText());
        }

        if (sb.Length == 0)
            sb.Append(result.IsError == true ? "(tool returned an error with no content)" : "(no content)");

        var text = sb.ToString().TrimEnd('\n');
        return result.IsError == true ? ToolResult.Error(text) : ToolResult.Ok(text);
    }

    private static void AppendBlock(StringBuilder sb, ContentBlock block)
    {
        switch (block)
        {
            case TextContentBlock text:
                sb.Append(text.Text);
                sb.Append('\n');
                break;
            case ImageContentBlock image:
                sb.Append("[image: ").Append(image.MimeType ?? "application/octet-stream")
                  .Append(", ").Append(image.Data.Length).Append(" b64-bytes]\n");
                break;
            case AudioContentBlock audio:
                sb.Append("[audio: ").Append(audio.MimeType ?? "application/octet-stream")
                  .Append(", ").Append(audio.Data.Length).Append(" b64-bytes]\n");
                break;
            case ResourceLinkBlock link:
                sb.Append("[resource link: ").Append(link.Uri);
                if (!string.IsNullOrEmpty(link.Name)) sb.Append(" (").Append(link.Name).Append(')');
                sb.Append("]\n");
                break;
            case EmbeddedResourceBlock embedded:
                AppendEmbeddedResource(sb, embedded);
                break;
            default:
                sb.Append('[').Append(block.Type ?? "unknown").Append(" content block]\n");
                break;
        }
    }

    private static void AppendEmbeddedResource(StringBuilder sb, EmbeddedResourceBlock embedded)
    {
        var resource = embedded.Resource;
        if (resource is TextResourceContents textRes)
        {
            sb.Append(textRes.Text);
            sb.Append('\n');
            return;
        }
        if (resource is BlobResourceContents blob)
        {
            sb.Append("[embedded resource ").Append(blob.Uri ?? "?")
              .Append(" (").Append(blob.MimeType ?? "application/octet-stream")
              .Append(", ").Append(blob.Blob.Length).Append(" b64-bytes)]\n");
            return;
        }
        sb.Append("[embedded resource]\n");
    }
}
