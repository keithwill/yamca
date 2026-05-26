using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";

    public string Description => "Write UTF-8 text content to a file, creating it if missing and overwriting if it exists. Parent directories are created as needed.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":    { "type": "string", "description": "Path (workspace-relative or absolute)." },
        "content": { "type": "string", "description": "The full file content to write." }
      },
      "required": ["path", "content"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);
        if (!ToolArguments.TryGetString(arguments, "content", out var content, out var contentError))
            return ToolResult.Error(contentError);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        try
        {
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            await File.WriteAllTextAsync(resolved, content, cancellationToken);
            return ToolResult.Ok($"Wrote {content.Length} characters to {resolved}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write '{resolved}': {ex.Message}");
        }
    }
}
