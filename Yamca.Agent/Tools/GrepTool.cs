using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

public sealed class GrepTool : ITool
{
    private const int DefaultMaxMatches = 100;
    private const int HardMaxMatches = 1000;
    private const int MaxLineLength = 1000;

    public string Name => "grep";

    public string Description => "Search file contents for a .NET regex pattern across files under a directory. Results: path:line:content per match. Binary files and common build/VCS directories are skipped; gitignore honored by default.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "pattern":           { "type": "string",  "description": ".NET regex to search per line. Anchors (^/$) match per line." },
        "path":              { "type": "string",  "description": "Directory to search (workspace-relative or absolute). Defaults to '.'." },
        "glob":              { "type": "string",  "description": "Glob to filter files. Default '**/*' (e.g. '**/*.cs')." },
        "case_insensitive":  { "type": "boolean", "description": "Case-insensitive match. Default false." },
        "respect_gitignore": { "type": "boolean", "description": "Honor .gitignore. Default true." },
        "max_matches":       { "type": "integer", "description": "Max matches. Default 100, cap 1000.", "minimum": 1, "maximum": 1000 }
      },
      "required": ["pattern"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "pattern", out var pattern, out var argError))
            return ToolResult.Error(argError);

        var requestedPath = ".";
        if (arguments.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            requestedPath = pathProp.GetString() ?? ".";

        var glob = "**/*";
        if (arguments.TryGetProperty("glob", out var globProp) && globProp.ValueKind == JsonValueKind.String)
        {
            var g = globProp.GetString();
            if (!string.IsNullOrWhiteSpace(g)) glob = g;
        }

        var caseInsensitive = false;
        if (arguments.TryGetProperty("case_insensitive", out var ciProp) &&
            (ciProp.ValueKind == JsonValueKind.True || ciProp.ValueKind == JsonValueKind.False))
            caseInsensitive = ciProp.GetBoolean();

        var respectGitignore = true;
        if (arguments.TryGetProperty("respect_gitignore", out var rgProp) &&
            (rgProp.ValueKind == JsonValueKind.True || rgProp.ValueKind == JsonValueKind.False))
            respectGitignore = rgProp.GetBoolean();

        var maxMatches = DefaultMaxMatches;
        if (arguments.TryGetProperty("max_matches", out var mmProp) && mmProp.ValueKind == JsonValueKind.Number)
            maxMatches = Math.Clamp(mmProp.GetInt32(), 1, HardMaxMatches);

        if (!ToolArguments.TryResolvePath(context, requestedPath, out var root, out var pathError))
            return ToolResult.Error(pathError);

        if (!Directory.Exists(root))
            return ToolResult.Error($"Directory not found: {root}");

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (caseInsensitive) options |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error($"Invalid regex: {ex.Message}");
        }

        var output = new StringBuilder();
        var matchCount = 0;
        var truncated = false;

        try
        {
            foreach (var file in FileSearch.Enumerate(root, glob, respectGitignore, cancellationToken))
            {
                if (matchCount >= maxMatches) { truncated = true; break; }

                if (await FileProbe.IsLikelyBinaryAsync(file, cancellationToken)) continue;

                var rel = FileSearch.ToForwardSlashRelative(root, file);

                FileStream stream;
                try
                {
                    stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        bufferSize: 4096, useAsync: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                await using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    var lineNumber = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? line;
                        try { line = await reader.ReadLineAsync(cancellationToken); }
                        catch (DecoderFallbackException) { break; }

                        if (line is null) break;
                        lineNumber++;

                        bool matched;
                        try { matched = regex.IsMatch(line); }
                        catch (RegexMatchTimeoutException) { break; }
                        if (!matched) continue;

                        var display = line.Length > MaxLineLength
                            ? line[..MaxLineLength] + "…"
                            : line;
                        output.Append(rel).Append(':').Append(lineNumber).Append(':').Append(display).Append('\n');

                        matchCount++;
                        if (matchCount >= maxMatches) { truncated = true; break; }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Search cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to search '{root}': {ex.Message}");
        }

        if (matchCount == 0)
            return ToolResult.Ok("(no matches)");

        if (truncated) output.Append("…[truncated at ").Append(maxMatches).Append(" matches]\n");
        return ToolResult.Ok(output.ToString());
    }

}
