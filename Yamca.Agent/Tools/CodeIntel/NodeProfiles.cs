using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Per-language knowledge the AST-aware search tools (code_find_* / code_search) need on
/// top of the symbol extractors: how to recognize comments, strings, identifiers, call
/// expressions and declarations for a given grammar. Implementations are lexical, not
/// semantic — they classify node kinds, they do not resolve symbols.
/// </summary>
public interface ILanguageNodeProfile
{
    /// <summary>Tree-sitter language id this profile handles, or <c>"*"</c> for the generic fallback.</summary>
    string LanguageId { get; }

    bool IsComment(string nodeType);
    bool IsString(string nodeType);
    bool IsIdentifier(string nodeType);

    /// <summary>If <paramref name="node"/> is a call expression, yields the called function's leaf name.</summary>
    bool TryGetCall(Node node, out string calleeName);

    /// <summary>If <paramref name="node"/> declares a named symbol, yields its name and a short kind token.</summary>
    bool TryGetDefinition(Node node, out string name, out string kind);
}

internal static class NodeHeuristics
{
    /// <summary>Right-most identifier text under <paramref name="node"/> (e.g. <c>Bar</c> in <c>Foo.Bar</c>).</summary>
    public static string? LastIdentifier(Node node)
    {
        if (IsIdentifierType(node.Type)) return node.Text;
        string? found = null;
        foreach (var child in node.NamedChildren)
        {
            var r = LastIdentifier(child);
            if (r is not null) found = r;
        }
        return found;
    }

    public static bool IsIdentifierType(string t) =>
        t == "identifier" || t.EndsWith("_identifier", StringComparison.Ordinal);

    /// <summary>
    /// Qualified container path of definition ancestors above <paramref name="node"/>, e.g.
    /// <c>Outer.Foo</c>. Walks the parent chain; cheap because trees are shallow.
    /// </summary>
    public static string EnclosingPath(Node node, ILanguageNodeProfile profile)
    {
        var names = new List<string>();
        var cur = node.Parent;
        while (cur is { } c)
        {
            if (profile.TryGetDefinition(c, out var n, out _) && !string.IsNullOrEmpty(n))
                names.Add(n);
            cur = c.Parent;
        }
        names.Reverse();
        return string.Join('.', names);
    }
}

/// <summary>
/// Grammar-agnostic fallback. Recognizes the node-type naming conventions shared by most
/// tree-sitter grammars. Used for any routed language without a dedicated profile; concrete
/// profiles below subclass it and override only where a grammar deviates.
/// </summary>
public class GenericNodeProfile : ILanguageNodeProfile
{
    public virtual string LanguageId => "*";

    public virtual bool IsComment(string t) => t.Contains("comment", StringComparison.Ordinal);
    public virtual bool IsString(string t) => t.Contains("string", StringComparison.Ordinal);
    public virtual bool IsIdentifier(string t) => NodeHeuristics.IsIdentifierType(t);

    public virtual bool TryGetCall(Node node, out string calleeName)
    {
        calleeName = string.Empty;
        var t = node.Type;
        if (t is not ("call_expression" or "call") && !t.EndsWith("_call", StringComparison.Ordinal))
            return false;
        var callee = node.GetChildForField("function") ?? node.GetChildForField("name");
        var id = callee is { } c ? NodeHeuristics.LastIdentifier(c) : null;
        calleeName = id ?? string.Empty;
        return calleeName.Length > 0;
    }

    public virtual bool TryGetDefinition(Node node, out string name, out string kind)
    {
        name = string.Empty;
        kind = string.Empty;
        var t = node.Type;
        if (!t.EndsWith("_declaration", StringComparison.Ordinal)
            && !t.EndsWith("_definition", StringComparison.Ordinal)
            && !t.EndsWith("_item", StringComparison.Ordinal))
            return false;

        var nameNode = node.GetChildForField("name");
        if (nameNode is null) return false;
        name = nameNode.Text ?? string.Empty;
        kind = DeriveKind(t);
        return name.Length > 0;
    }

    protected static string DeriveKind(string nodeType)
    {
        // class_declaration -> class, function_item -> function, method_definition -> method.
        var cut = nodeType.LastIndexOf('_');
        return cut > 0 ? nodeType[..cut] : nodeType;
    }
}

/// <summary>C# names its call node <c>invocation_expression</c>, not <c>*_call</c>.</summary>
public sealed class CSharpNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "c-sharp";

    public override bool TryGetCall(Node node, out string calleeName)
    {
        calleeName = string.Empty;
        if (node.Type != "invocation_expression") return false;
        var fn = node.GetChildForField("function");
        var id = fn is { } f ? NodeHeuristics.LastIdentifier(f) : null;
        calleeName = id ?? string.Empty;
        return calleeName.Length > 0;
    }
}

// The remaining languages follow the conventions GenericNodeProfile already handles
// (call_expression / call, *_declaration|*_definition|*_item, identifier, comment/string
// substrings). They exist as named entries so the resolver maps them explicitly and they
// have a dedicated home for future grammar-specific refinements.
public sealed class PythonNodeProfile : GenericNodeProfile { public override string LanguageId => "python"; }
public sealed class JavaScriptNodeProfile : GenericNodeProfile { public override string LanguageId => "javascript"; }
public sealed class TypeScriptNodeProfile : GenericNodeProfile { public override string LanguageId => "typescript"; }
public sealed class TsxNodeProfile : GenericNodeProfile { public override string LanguageId => "tsx"; }
public sealed class RustNodeProfile : GenericNodeProfile { public override string LanguageId => "rust"; }
public sealed class GoNodeProfile : GenericNodeProfile { public override string LanguageId => "go"; }

/// <summary>
/// Resolves a tree-sitter language id to its <see cref="ILanguageNodeProfile"/>, falling
/// back to the generic profile for languages without a dedicated one.
/// </summary>
public sealed class NodeProfileResolver
{
    private readonly Dictionary<string, ILanguageNodeProfile> _byLanguage;
    private readonly ILanguageNodeProfile _fallback;

    public NodeProfileResolver(IEnumerable<ILanguageNodeProfile> profiles)
    {
        var list = profiles.ToList();
        _fallback = list.FirstOrDefault(p => p.LanguageId == "*") ?? new GenericNodeProfile();
        _byLanguage = list.Where(p => p.LanguageId != "*")
            .ToDictionary(p => p.LanguageId, StringComparer.Ordinal);
    }

    public ILanguageNodeProfile Resolve(string languageId) =>
        _byLanguage.TryGetValue(languageId, out var profile) ? profile : _fallback;
}
