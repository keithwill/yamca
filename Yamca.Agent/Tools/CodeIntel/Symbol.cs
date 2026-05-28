using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// One symbol extracted from a parsed source file. <paramref name="Depth"/> drives the
/// indent level in the rendered output; <paramref name="Line"/> / <paramref name="EndLine"/>
/// are 1-indexed and bound the symbol's source span (used by code_extract_symbol /
/// code_surrounding_context / code_edit_symbol). <paramref name="Name"/> is the bare leaf
/// identifier used for name lookup (empty for anonymous symbols).
/// </summary>
public sealed record Symbol(string Kind, string Display, string Name, int Line, int EndLine, int Depth)
{
    /// <summary>
    /// Build a symbol from the declaration <paramref name="node"/>, deriving the 1-indexed
    /// start/end lines from its span so callers don't compute them by hand.
    /// </summary>
    public static Symbol From(string kind, string display, string name, Node node, int depth) =>
        new(kind, display, name, node.StartPosition.Row + 1, node.EndPosition.Row + 1, depth);
}
