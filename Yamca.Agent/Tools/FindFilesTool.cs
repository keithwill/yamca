using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class FindFilesTool : ITool
{
    private const int DefaultMaxResults = 200;
    private const int HardMaxResults = 2000;

    public string Name => "find_files";

    public string Description => "Recursively find files under a directory whose paths match a glob pattern. Returns workspace-relative paths. Common build/VCS directories are skipped; gitignore is honored by default.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "pattern":           { "type": "string",  "description": "Glob pattern relative to the search root (e.g. '**/*.cs')." },
        "path":              { "type": "string",  "description": "Directory to search (workspace-relative or absolute). Defaults to '.'." },
        "respect_gitignore": { "type": "boolean", "description": "Honor .gitignore. Default true." },
        "max_results":       { "type": "integer", "description": "Max matches. Default 200, cap 2000.", "minimum": 1, "maximum": 2000 }
      },
      "required": ["pattern"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "pattern", out var pattern, out var argError))
            return Task.FromResult(ToolResult.Error(argError));

        var requestedPath = ".";
        if (arguments.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            requestedPath = pathProp.GetString() ?? ".";

        var respectGitignore = true;
        if (arguments.TryGetProperty("respect_gitignore", out var rgProp) &&
            (rgProp.ValueKind == JsonValueKind.True || rgProp.ValueKind == JsonValueKind.False))
            respectGitignore = rgProp.GetBoolean();

        var maxResults = DefaultMaxResults;
        if (arguments.TryGetProperty("max_results", out var mrProp) && mrProp.ValueKind == JsonValueKind.Number)
            maxResults = Math.Clamp(mrProp.GetInt32(), 1, HardMaxResults);

        if (!ToolArguments.TryResolvePath(context, requestedPath, out var root, out var pathError))
            return Task.FromResult(ToolResult.Error(pathError));

        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Error($"Directory not found: {root}"));

        try
        {
            var matches = new List<string>(maxResults);
            var truncated = false;

            foreach (var file in FileSearch.Enumerate(root, pattern, respectGitignore, cancellationToken))
            {
                if (matches.Count >= maxResults) { truncated = true; break; }
                matches.Add(FileSearch.ToForwardSlashRelative(root, file));
            }

            if (matches.Count == 0)
                return Task.FromResult(ToolResult.Ok("(no matches)"));

            matches.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            foreach (var m in matches) sb.Append(m).Append('\n');
            if (truncated) sb.Append("…[truncated at ").Append(maxResults).Append(" matches]\n");
            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ToolResult.Error("Search cancelled."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Failed to search '{root}': {ex.Message}"));
        }
    }
}
