using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class DeleteFileTool : ITool
{
    public string Name => "delete_file";

    public string Description => "Delete a single file (not directories).";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path (workspace-relative or absolute)." }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return Task.FromResult(ToolResult.Error(argError));

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return Task.FromResult(ToolResult.Error(pathError));

        if (Directory.Exists(resolved))
            return Task.FromResult(ToolResult.Error($"Refusing to delete: '{resolved}' is a directory. This tool deletes files only."));

        if (!File.Exists(resolved))
            return Task.FromResult(ToolResult.Error($"File not found: {resolved}"));

        try
        {
            File.Delete(resolved);
            return Task.FromResult(ToolResult.Ok($"Deleted {resolved}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Failed to delete '{resolved}': {ex.Message}"));
        }
    }
}
