using System.Collections.Frozen;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Maps file paths to tree-sitter language identifiers. The identifier is the same
/// string accepted by <c>TreeSitter.Language(string)</c> — see the TreeSitter.DotNet
/// runtimes directory for the canonical set.
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
        [".scala"] = "scala",
        // Web
        [".js"]   = "javascript",
        [".jsx"]  = "javascript",
        [".mjs"]  = "javascript",
        [".cjs"]  = "javascript",
        [".ts"]   = "typescript",
        [".tsx"]  = "tsx",
        [".html"] = "html",
        [".htm"]  = "html",
        [".css"]  = "css",
        // Scripting
        [".py"]   = "python",
        [".rb"]   = "ruby",
        [".php"]  = "php",
        [".sh"]   = "bash",
        [".bash"] = "bash",
        // Systems
        [".rs"]   = "rust",
        [".go"]   = "go",
        // Functional
        [".hs"]   = "haskell",
        [".ml"]   = "ocaml",
        [".mli"]  = "ocaml",
        [".jl"]   = "julia",
        // Data
        [".json"] = "json",
        // Misc
        [".ql"]   = "ql",
        [".v"]    = "verilog",
        [".sv"]   = "verilog",
        [".agda"] = "agda",
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
