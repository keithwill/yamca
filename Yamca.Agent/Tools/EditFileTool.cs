using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";

    public string Description => "Replace an exact string in an existing file. The 'old_string' must match exactly (including whitespace) and must appear exactly once in the file unless 'replace_all' is true. Use 'write_file' to create new files or rewrite entirely.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":        { "type": "string", "description": "File path, relative to the workspace root or absolute." },
        "old_string":  { "type": "string", "description": "Exact text to find. Include enough surrounding context to be unique in the file." },
        "new_string":  { "type": "string", "description": "Replacement text. May be empty to delete the matched text." },
        "replace_all": { "type": "boolean", "description": "If true, replace every occurrence. Default false (requires unique match).", "default": false }
      },
      "required": ["path", "old_string", "new_string"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);
        if (!ToolArguments.TryGetString(arguments, "old_string", out var oldString, out var oldError))
            return ToolResult.Error(oldError);
        if (!ToolArguments.TryGetString(arguments, "new_string", out var newString, out var newError))
            return ToolResult.Error(newError);
        if (!ToolArguments.TryGetBool(arguments, "replace_all", defaultValue: false, out var replaceAll, out var boolError))
            return ToolResult.Error(boolError);

        if (oldString.Length == 0)
            return ToolResult.Error("old_string must not be empty.");
        if (oldString == newString)
            return ToolResult.Error("old_string and new_string are identical; nothing to do.");

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        try
        {
            var text = await File.ReadAllTextAsync(resolved, cancellationToken);
            var count = CountOccurrences(text, oldString);

            if (count == 0)
                return ToolResult.Error($"old_string not found in {resolved}");
            if (count > 1 && !replaceAll)
                return ToolResult.Error($"old_string matched {count} times in {resolved}; pass replace_all=true or include more surrounding context to make it unique.");

            var updated = text.Replace(oldString, newString, StringComparison.Ordinal);
            await File.WriteAllTextAsync(resolved, updated, cancellationToken);

            return ToolResult.Ok($"Edited {resolved} ({count} replacement{(count == 1 ? "" : "s")})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to edit '{resolved}': {ex.Message}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
