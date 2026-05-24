using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description => "Read the full contents of a UTF-8 text file at the given path.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path, relative to the workspace root or absolute." }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        try
        {
            var content = await File.ReadAllTextAsync(resolved, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read '{resolved}': {ex.Message}");
        }
    }
}
