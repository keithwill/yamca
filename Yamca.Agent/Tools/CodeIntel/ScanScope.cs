using System.Text.Json;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Parsed common arguments shared by the AST-aware search tools: where to scan, which files,
/// and how many matches to return. Mirrors <c>GrepTool</c>'s conventions.
/// </summary>
internal readonly record struct ScanScope(string Root, string Glob, bool RespectGitignore, int MaxMatches)
{
    public const int DefaultMaxMatches = 100;
    public const int HardMaxMatches = 1000;

    public static bool TryParse(JsonElement args, ToolContext ctx, out ScanScope scope, out string error)
    {
        scope = default;
        error = string.Empty;

        var requestedPath = ".";
        if (args.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            requestedPath = pathProp.GetString() ?? ".";

        var glob = "**/*";
        if (args.TryGetProperty("glob", out var globProp) && globProp.ValueKind == JsonValueKind.String)
        {
            var g = globProp.GetString();
            if (!string.IsNullOrWhiteSpace(g)) glob = g;
        }

        var respectGitignore = true;
        if (args.TryGetProperty("respect_gitignore", out var rgProp) &&
            (rgProp.ValueKind == JsonValueKind.True || rgProp.ValueKind == JsonValueKind.False))
            respectGitignore = rgProp.GetBoolean();

        var maxMatches = DefaultMaxMatches;
        if (args.TryGetProperty("max_matches", out var mmProp) && mmProp.ValueKind == JsonValueKind.Number)
            maxMatches = Math.Clamp(mmProp.GetInt32(), 1, HardMaxMatches);

        if (!ToolArguments.TryResolvePath(ctx, requestedPath, out var root, out var pathError))
        {
            error = pathError;
            return false;
        }
        if (!Directory.Exists(root))
        {
            error = $"Directory not found: {root}. Pass a directory to scan (default '.').";
            return false;
        }

        scope = new ScanScope(root, glob, respectGitignore, maxMatches);
        return true;
    }

    /// <summary>JSON-schema fragment for the shared scope properties (to splice into a tool schema).</summary>
    public const string SchemaProperties = """
        "path":              { "type": "string",  "description": "Directory to scan (workspace-relative or absolute). Defaults to '.'." },
        "glob":              { "type": "string",  "description": "Glob to filter files. Default '**/*' (e.g. '**/*.cs')." },
        "respect_gitignore": { "type": "boolean", "description": "Honor .gitignore. Default true." },
        "max_matches":       { "type": "integer", "description": "Max matches. Default 100, cap 1000.", "minimum": 1, "maximum": 1000 }
        """;
}
