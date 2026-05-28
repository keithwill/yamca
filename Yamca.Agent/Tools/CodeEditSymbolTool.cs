using System.Text.Json;
using TreeSitter;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tools;

/// <summary>
/// Replace the full source of one uniquely-named symbol (signature + body) without the model
/// having to describe exact line ranges. After building the new file content it re-parses in
/// memory and refuses the write if the edit introduces parse errors that weren't there before.
/// </summary>
public sealed class CodeEditSymbolTool : ITool
{
    private const int MaxCandidates = 25;

    private readonly SymbolService _symbols;

    public CodeEditSymbolTool(SymbolService symbols)
    {
        _symbols = symbols;
    }

    public string Name => "code_edit_symbol";

    public string Description => "Replace the entire declaration (signature and body) of one named symbol in a file with 'new_source'. The name must resolve to exactly one symbol (use a qualified name like Foo.Bar to disambiguate). 'new_source' must be complete and correctly indented for its position. The edit is rejected if it introduces new parse errors. For free-form edits use edit_file.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "path":       { "type": "string", "description": "Source file path (workspace-relative or absolute)." },
        "name":       { "type": "string", "description": "Symbol to replace. Leaf ('Bar') or qualified ('Foo.Bar'); must be unique." },
        "new_source": { "type": "string", "description": "Full replacement source for the symbol (signature + body), correctly indented." }
      },
      "required": ["path", "name", "new_source"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;
    public PermissionLevel DefaultPermission => PermissionLevel.Ask;
    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "path", out var requested, out var argError))
            return ToolResult.Error(argError);
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var nameError))
            return ToolResult.Error(nameError);
        if (!ToolArguments.TryGetString(arguments, "new_source", out var newSource, out var srcError))
            return ToolResult.Error(srcError);
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("'name' must not be empty.");

        if (!ToolArguments.TryResolvePath(context, requested, out var resolved, out var pathError))
            return ToolResult.Error(pathError);
        if (!File.Exists(resolved))
            return ToolResult.Error($"File not found: {resolved}");

        var load = await _symbols.LoadAsync(resolved, cancellationToken);
        if (!load.IsOk)
            return ToolResult.Error(load.Note ?? $"Could not read symbols from '{resolved}'.");

        var qualified = SymbolLookup.Qualify(load.Symbols);
        var matches = qualified.Where(qs => SymbolLookup.Matches(qs, name)).ToList();

        if (matches.Count == 0)
            return ToolResult.Error($"No symbol named '{name}' in {Path.GetFileName(resolved)}.");
        if (matches.Count > 1)
        {
            var candidates = string.Join("\n", matches.Take(MaxCandidates).Select(qs => $"  {qs.QualifiedName}  [L{qs.Symbol.Line}]"));
            return ToolResult.Error($"'{name}' matches {matches.Count} symbols; qualify to pick one:\n{candidates}");
        }

        var hit = matches[0].Symbol;
        var updated = ReplaceLines(load.Source, hit.Line, hit.EndLine, newSource);

        // Local re-parse guard: don't let a replacement silently break the file.
        if (!load.HasErrors && IntroducesParseError(load.LanguageId!, updated))
            return ToolResult.Error(
                $"Edit rejected: replacing '{name}' (lines {hit.Line}-{hit.EndLine}) introduces a parse error. " +
                "The original file parsed cleanly; check that 'new_source' is a complete, balanced declaration.");

        try
        {
            await File.WriteAllTextAsync(resolved, updated, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write '{resolved}': {ex.Message}");
        }

        var newLineCount = newSource.Replace("\r\n", "\n").Count(c => c == '\n') + 1;
        return ToolResult.Ok($"Edited {resolved} (symbol '{name}', replaced lines {hit.Line}-{hit.EndLine} with {newLineCount} line{(newLineCount == 1 ? "" : "s")}).");
    }

    /// <summary>
    /// Replace 1-indexed lines [<paramref name="startLine"/>, <paramref name="endLine"/>] of
    /// <paramref name="source"/> with <paramref name="replacement"/>, preserving the file's
    /// dominant line ending.
    /// </summary>
    private static string ReplaceLines(string source, int startLine, int endLine, string replacement)
    {
        var newlineSeq = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = Math.Clamp(startLine, 1, lines.Length);
        var end = Math.Clamp(endLine, start, lines.Length);

        // Normalize the replacement to LF and drop a single trailing newline so we don't inject
        // a spurious blank line between it and the following code.
        var body = replacement.Replace("\r\n", "\n").Replace('\r', '\n');
        if (body.EndsWith('\n')) body = body[..^1];
        var bodyLines = body.Split('\n');

        var result = new List<string>(lines.Length - (end - start + 1) + bodyLines.Length);
        result.AddRange(lines[..(start - 1)]);
        result.AddRange(bodyLines);
        result.AddRange(lines[end..]);

        return string.Join(newlineSeq, result);
    }

    private static bool IntroducesParseError(string languageId, string content)
    {
        try
        {
            using var language = new Language(languageId);
            using var parser = new Parser(language);
            using var tree = parser.Parse(content);
            return tree is null || tree.RootNode.HasError;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or ArgumentException)
        {
            // If we can't parse to verify, don't block the edit on the guard.
            return false;
        }
    }
}
