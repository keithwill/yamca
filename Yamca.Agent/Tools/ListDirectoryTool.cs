using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class ListDirectoryTool : ITool
{
    private const int DefaultMaxEntries = 200;
    private const int HardMaxEntries = 1000;

    public string Name => "list_directory";

    public string Description => "List the immediate entries (files and subdirectories) of a directory. Directories are marked with a trailing separator. Returns up to 200 entries by default (hard cap 1000); pass 'max_results' to adjust.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":        { "type": "string",  "description": "Directory path, relative to the workspace root or absolute. Use '.' for the workspace root." },
        "max_results": { "type": "integer", "description": "Maximum number of entries to return. Default 200, hard cap 1000.", "minimum": 1, "maximum": 1000 }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return Task.FromResult(ToolResult.Error(argError));

        var maxResults = DefaultMaxEntries;
        if (arguments.TryGetProperty("max_results", out var mrProp) && mrProp.ValueKind == JsonValueKind.Number)
            maxResults = Math.Clamp(mrProp.GetInt32(), 1, HardMaxEntries);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return Task.FromResult(ToolResult.Error(pathError));

        if (!Directory.Exists(resolved))
            return Task.FromResult(ToolResult.Error($"Directory not found: {resolved}"));

        try
        {
            var entries = new DirectoryInfo(resolved)
                .EnumerateFileSystemInfos()
                .OrderBy(e => e is DirectoryInfo ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults + 1)
                .ToList();

            var truncated = entries.Count > maxResults;
            if (truncated) entries.RemoveAt(entries.Count - 1);

            var sb = new StringBuilder();
            sb.Append(resolved).Append(':').Append('\n');
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo)
                    sb.Append(entry.Name).Append('/').Append('\n');
                else
                    sb.Append(entry.Name).Append('\n');
            }

            if (entries.Count == 0 && !truncated)
                sb.Append("(empty)\n");

            if (truncated)
                sb.Append("…[truncated at ").Append(maxResults).Append(" entries; narrow with find_files or grep]\n");

            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Failed to list '{resolved}': {ex.Message}"));
        }
    }
}
