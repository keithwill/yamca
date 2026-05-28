namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// A symbol paired with the container chain it sits under, reconstructed from the
/// depth-ordered symbol list. Lets the code_* tools build qualified names
/// (<c>Outer.Inner.Method</c>) and human-readable container paths without the extractor
/// having to record parent pointers.
/// </summary>
public readonly record struct QualifiedSymbol(Symbol Symbol, IReadOnlyList<Symbol> Ancestors)
{
    /// <summary>Dotted name including ancestors, skipping anonymous (empty-name) links.</summary>
    public string QualifiedName =>
        string.Join('.', Ancestors.Select(a => a.Name).Append(Symbol.Name).Where(n => !string.IsNullOrEmpty(n)));

    /// <summary>Ancestor signatures joined for display, e.g. <c>class Foo &gt; class Bar</c>.</summary>
    public string ContainerPath => string.Join(" > ", Ancestors.Select(a => a.Display));
}

internal static class SymbolLookup
{
    /// <summary>
    /// Pair each symbol with its ancestors. Relies on the extractor convention that a
    /// container at depth <c>d</c> is followed by its members at depth <c>d+1</c>.
    /// </summary>
    public static IReadOnlyList<QualifiedSymbol> Qualify(IReadOnlyList<Symbol> symbols)
    {
        var result = new List<QualifiedSymbol>(symbols.Count);
        var stack = new List<Symbol>();
        foreach (var s in symbols)
        {
            while (stack.Count >= s.Depth) stack.RemoveAt(stack.Count - 1);
            result.Add(new QualifiedSymbol(s, stack.ToArray()));
            stack.Add(s);
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="query"/> names <paramref name="qs"/> — either as a bare leaf
    /// name or as a dotted suffix of its qualified name (<c>Bar</c> or <c>Foo.Bar</c> both match
    /// <c>Outer.Foo.Bar</c>).
    /// </summary>
    public static bool Matches(QualifiedSymbol qs, string query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        if (!query.Contains('.'))
            return string.Equals(qs.Symbol.Name, query, StringComparison.Ordinal);

        var qualified = qs.QualifiedName;
        return qualified.Equals(query, StringComparison.Ordinal)
            || qualified.EndsWith('.' + query, StringComparison.Ordinal);
    }

    /// <summary>Extract source lines [<paramref name="startLine"/>, <paramref name="endLine"/>] (1-indexed, inclusive), normalized to LF.</summary>
    public static string SliceLines(string source, int startLine, int endLine)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = Math.Clamp(startLine, 1, lines.Length);
        var end = Math.Clamp(endLine, start, lines.Length);
        return string.Join('\n', lines[(start - 1)..end]);
    }
}
