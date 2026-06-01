using System.Text.Json;

namespace Yamca.Web.Services;

/// <summary>A file-mutating tool call reduced to a before/after text pair, ready to feed
/// <see cref="DiffModelBuilder"/>. <c>edit_file</c> carries both sides in its arguments;
/// <c>write_file</c> carries only the new content, so the old side is empty (an all-additions diff,
/// which reads correctly for the common new-file case).</summary>
public sealed record FileChange(string ToolName, string? Path, string OldText, string NewText);

/// <summary>Extracts a <see cref="FileChange"/> from a file-mutating tool call's arguments, from
/// either the raw streamed JSON (chat tool-call card) or a parsed element (approval prompt). Returns
/// null for non-diff tools or when the arguments are incomplete (tool-call args stream in, so a
/// pending call's JSON may not yet parse) — callers fall back to showing raw arguments.</summary>
public static class FileChangeArgs
{
    public static bool IsDiffTool(string toolName) => toolName is "edit_file" or "write_file";

    public static FileChange? Parse(string toolName, string? argumentsJson)
    {
        if (!IsDiffTool(toolName) || string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return Parse(toolName, doc.RootElement);
        }
        catch (JsonException)
        {
            return null; // partial/streaming arguments — not parseable yet
        }
    }

    public static FileChange? Parse(string toolName, JsonElement args)
    {
        if (!IsDiffTool(toolName) || args.ValueKind != JsonValueKind.Object) return null;
        var path = GetString(args, "path");

        if (toolName == "edit_file")
        {
            var oldText = GetString(args, "old_string");
            var newText = GetString(args, "new_string");
            if (oldText is null || newText is null) return null;
            return new FileChange(toolName, path, oldText, newText);
        }

        // write_file
        var content = GetString(args, "content");
        if (content is null) return null;
        return new FileChange(toolName, path, "", content);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
