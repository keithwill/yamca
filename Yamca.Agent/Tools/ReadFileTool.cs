using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class ReadFileTool : ITool
{
    private const int DefaultMaxLines = 2000;
    private const int HardMaxLines = 2000;
    private const int MaxLineLength = 2000;

    public string Name => "read_file";

    public string Description => "Read a UTF-8 text file. Output is formatted with 1-indexed line numbers (like 'cat -n'). By default returns up to the first 2000 lines; use 'offset' and 'limit' to page through larger files. Long individual lines are truncated. Binary files are refused. To modify or remove a file, use edit_file, write_file, or delete_file — if any are not in the current tool list, call load_tool to make them available.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":   { "type": "string",  "description": "Path (workspace-relative or absolute)." },
        "offset": { "type": "integer", "description": "Line to start from (1-indexed). Default 1.", "minimum": 1 },
        "limit":  { "type": "integer", "description": "Max lines to return. Default and hard cap 2000.", "minimum": 1, "maximum": 2000 }
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

        var offset = 1;
        if (arguments.TryGetProperty("offset", out var offProp) && offProp.ValueKind == JsonValueKind.Number)
            offset = Math.Max(1, offProp.GetInt32());

        var limit = DefaultMaxLines;
        if (arguments.TryGetProperty("limit", out var limProp) && limProp.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limProp.GetInt32(), 1, HardMaxLines);

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        if (await FileProbe.IsLikelyBinaryAsync(resolved, cancellationToken))
            return ToolResult.Error($"Cannot read binary file: {resolved}");

        try
        {
            await using var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var sb = new StringBuilder();
            var currentLine = 0;
            var emitted = 0;
            var lastEmittedLine = 0;

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                currentLine++;

                if (currentLine < offset) continue;
                if (emitted >= limit) break;

                if (line.Length > MaxLineLength)
                    line = line.AsSpan(0, MaxLineLength).ToString() + "…";

                sb.Append($"{currentLine,6}\t").Append(line).Append('\n');
                emitted++;
                lastEmittedLine = currentLine;
            }

            // Drain remaining lines for an accurate total (no buffering).
            var totalLines = currentLine;
            if (line is not null)
            {
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                    totalLines++;
                // The line we broke on was already counted via currentLine++ above.
            }

            if (totalLines == 0)
                return ToolResult.Ok("(empty file)");

            if (emitted == 0)
                return ToolResult.Ok($"(file has {totalLines} lines; offset {offset} is past end)");

            if (lastEmittedLine < totalLines)
                sb.Append($"…[showing lines {offset}-{lastEmittedLine} of {totalLines}; use offset={lastEmittedLine + 1} to continue]\n");

            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read '{resolved}': {ex.Message}");
        }
    }
}
