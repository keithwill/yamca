using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Stateless tree-sitter navigation helpers shared by the per-language
/// <see cref="ISymbolExtractor"/> implementations. These are deliberately just the grammar-agnostic
/// "find the name / find a descendant" utilities every extractor re-implemented identically — the
/// per-language node-type tables (which nodes are containers vs members) stay in each extractor,
/// since those legitimately differ between grammars.
/// </summary>
internal static class NodeHelpers
{
    /// <summary>Leaf text of the node's <c>name</c> field, or empty when it has none. This is the
    /// "bare name" used as a <see cref="Symbol"/>'s lookup key.</summary>
    public static string NameOrEmpty(this Node node) => node.GetChildForField("name")?.Text ?? string.Empty;

    /// <summary>Leaf text of the node's <c>name</c> field, or <c>&lt;anonymous&gt;</c> when it has
    /// none. The display-friendly counterpart of <see cref="NameOrEmpty"/>.</summary>
    public static string NameOrAnonymous(this Node node) => node.GetChildForField("name")?.Text ?? "<anonymous>";

    /// <summary>First descendant of the given type in a depth-first walk over named children, or
    /// <c>null</c> if none exists.</summary>
    public static Node? FirstDescendant(this Node node, string type)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == type) return child;
            var found = child.FirstDescendant(type);
            if (found is not null) return found;
        }
        return null;
    }
}
