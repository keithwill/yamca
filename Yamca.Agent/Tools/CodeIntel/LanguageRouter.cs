using System.Collections.Frozen;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Maps file paths to tree-sitter language identifiers. The identifier is the same
/// string accepted by <c>TreeSitter.Language(string)</c>.
/// <para>
/// Every language here must have both a registered <see cref="ISymbolExtractor"/> (see the
/// DI wiring in <c>Program.cs</c>) and an <see cref="ILanguageNodeProfile"/>, and its grammar
/// binary must survive the prune in <c>Yamca.Web.csproj</c>. Routing an extension whose
/// grammar isn't bundled just dead-ends in <c>CodeScan</c>'s missing-grammar catch, so keep
/// this set, the registered extractors, and the kept-grammar list in lockstep.
/// </para>
/// </summary>
public static class LanguageRouter
{
    private static readonly FrozenDictionary<string, string> ExtensionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // C-family
        [".c"]    = "c",
        [".h"]    = "c",
        [".cpp"]  = "cpp",
        [".cc"]   = "cpp",
        [".cxx"]  = "cpp",
        [".hpp"]  = "cpp",
        [".hh"]   = "cpp",
        [".hxx"]  = "cpp",
        [".cs"]   = "c-sharp",
        [".java"] = "java",
        // Web
        [".js"]   = "javascript",
        [".jsx"]  = "javascript",
        [".mjs"]  = "javascript",
        [".cjs"]  = "javascript",
        [".ts"]   = "typescript",
        [".tsx"]  = "tsx",
        // Scripting
        [".py"]   = "python",
        [".rb"]   = "ruby",
        [".php"]  = "php",
        // Systems
        [".rs"]   = "rust",
        [".go"]   = "go",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the tree-sitter language id for <paramref name="path"/>, or <see langword="null"/>
    /// if the path's extension is not in the supported set.
    /// </summary>
    public static string? GetLanguageId(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        return ExtensionMap.TryGetValue(ext, out var lang) ? lang : null;
    }

    /// <summary>Languages this router will route to. Useful for diagnostics and tests.</summary>
    public static IReadOnlyCollection<string> SupportedLanguageIds { get; } =
        ExtensionMap.Values.Distinct(StringComparer.Ordinal).ToArray();
}
