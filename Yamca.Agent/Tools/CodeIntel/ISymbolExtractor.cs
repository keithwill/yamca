using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Pulls a list of containers and members out of a parsed syntax tree for a single
/// language. One implementation per language; the tree-sitter <see cref="Language"/>
/// is constructed by the caller from <see cref="LanguageId"/>.
/// </summary>
public interface ISymbolExtractor
{
    /// <summary>The tree-sitter language id (the same string passed to <c>new Language(...)</c>).</summary>
    string LanguageId { get; }

    /// <summary>
    /// Walks <paramref name="root"/> and yields symbols in source order. Implementations may
    /// run a tree-sitter <see cref="Query"/> internally or walk the tree directly.
    /// </summary>
    IEnumerable<Symbol> Extract(Node root, string source);
}
