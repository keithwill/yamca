using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Shared file-walking for the AST-aware search tools (code_find_* / code_search). Mirrors
/// <c>GrepTool</c>'s enumeration but hands each file's parsed tree to a visitor. The visitor
/// runs inside the tree's <c>using</c> scope, so it must collect any results as managed data
/// before returning — the native <c>Tree</c> is disposed immediately afterwards.
/// </summary>
internal static class CodeScan
{
    private const long MaxFileBytes = 2L * 1024 * 1024;

    /// <summary>Return <see langword="false"/> to stop the scan early (e.g. result cap reached).</summary>
    public delegate bool Visitor(string relPath, string source, Node root, ILanguageNodeProfile profile);

    public static async Task RunAsync(
        string root,
        string glob,
        bool respectGitignore,
        NodeProfileResolver resolver,
        Visitor visit,
        CancellationToken ct)
    {
        foreach (var file in FileSearch.Enumerate(root, glob, respectGitignore, ct))
        {
            ct.ThrowIfCancellationRequested();

            var languageId = LanguageRouter.GetLanguageId(file);
            if (languageId is null) continue;

            long length;
            try { length = new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (length > MaxFileBytes) continue;

            if (await FileProbe.IsLikelyBinaryAsync(file, ct)) continue;

            string source;
            try { source = await File.ReadAllTextAsync(file, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var profile = resolver.Resolve(languageId);
            var rel = FileSearch.ToForwardSlashRelative(root, file);

            try
            {
                using var language = new Language(languageId);
                using var parser = new Parser(language);
                using var tree = parser.Parse(source);
                if (tree is null) continue;

                if (!visit(rel, source, tree.RootNode, profile))
                    return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or ArgumentException)
            {
                // Grammar failed to load (unbundled language) — skip this file.
            }
        }
    }

    /// <summary>
    /// Depth-first enumeration of named descendants of <paramref name="root"/>. When
    /// <paramref name="prune"/> returns true for a node, that node is yielded-skipped and its
    /// subtree is not descended (used to drop comment/string subtrees).
    /// </summary>
    public static IEnumerable<Node> NamedDescendants(Node root, Func<Node, bool>? prune = null)
    {
        foreach (var child in root.NamedChildren)
        {
            if (prune is not null && prune(child)) continue;
            yield return child;
            foreach (var d in NamedDescendants(child, prune))
                yield return d;
        }
    }
}
