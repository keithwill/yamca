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

/// <summary>Java names its call node <c>method_invocation</c>, not <c>*_call</c>.</summary>
public sealed class JavaNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "java";

    public override bool TryGetCall(Node node, out string calleeName)
    {
        calleeName = string.Empty;
        if (node.Type != "method_invocation") return false;
        // method_invocation exposes the called method's leaf via the `name` field
        // (the optional `object` field is the receiver, which we ignore).
        var id = node.GetChildForField("name")?.Text;
        calleeName = id ?? string.Empty;
        return calleeName.Length > 0;
    }
}

/// <summary>
/// C uses the generic <c>call_expression</c> convention, but its declarations carry no
/// <c>name</c> field — the identifier is buried in a declarator chain — so the generic
/// <c>TryGetDefinition</c> misses functions and aggregates entirely.
/// </summary>
public sealed class CNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "c";

    public override bool TryGetDefinition(Node node, out string name, out string kind)
    {
        name = string.Empty;
        kind = string.Empty;
        switch (node.Type)
        {
            case "function_definition":
                name = CSymbolExtractor.DeclaratorLeaf(node.GetChildForField("declarator"));
                kind = "func";
                return name.Length > 0;
            case "type_definition":
                name = CSymbolExtractor.TypedefName(node);
                kind = "typedef";
                return name.Length > 0;
            case "struct_specifier":
            case "union_specifier":
            case "enum_specifier":
                name = node.GetChildForField("name")?.Text ?? string.Empty;
                kind = node.Type[..node.Type.IndexOf('_')];
                return name.Length > 0;
            default:
                return false;
        }
    }
}

/// <summary>
/// C++ uses the generic <c>call_expression</c> convention, but — like C — buries declaration
/// names in declarator chains, so the generic <c>TryGetDefinition</c> misses functions,
/// methods and aggregates. Reuses <see cref="CppSymbolExtractor.DeclaratorLeaf"/> so qualified
/// (<c>Foo::bar</c>) and destructor names resolve.
/// </summary>
public sealed class CppNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "cpp";

    public override bool TryGetDefinition(Node node, out string name, out string kind)
    {
        name = string.Empty;
        kind = string.Empty;
        switch (node.Type)
        {
            case "function_definition":
            case "declaration":
            case "field_declaration":
                name = CppSymbolExtractor.DeclaratorLeaf(node.GetChildForField("declarator"));
                kind = "func";
                return name.Length > 0;
            case "namespace_definition":
                name = node.GetChildForField("name")?.Text ?? string.Empty;
                kind = "namespace";
                return name.Length > 0;
            case "class_specifier":
            case "struct_specifier":
            case "union_specifier":
            case "enum_specifier":
                name = node.GetChildForField("name")?.Text ?? string.Empty;
                kind = node.Type[..node.Type.IndexOf('_')];
                return name.Length > 0;
            default:
                return false;
        }
    }
}

/// <summary>
/// Ruby's <c>call</c> node names the callee via a <c>method</c> field (not <c>function</c>),
/// and its definitions (<c>method</c> / <c>class</c> / <c>module</c>) carry no <c>_declaration</c>
/// suffix — so both generic heuristics miss it.
/// </summary>
public sealed class RubyNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "ruby";

    public override bool TryGetCall(Node node, out string calleeName)
    {
        calleeName = string.Empty;
        if (node.Type != "call") return false;
        calleeName = node.GetChildForField("method")?.Text ?? string.Empty;
        return calleeName.Length > 0;
    }

    public override bool TryGetDefinition(Node node, out string name, out string kind)
    {
        name = string.Empty;
        kind = string.Empty;
        switch (node.Type)
        {
            case "method":
            case "singleton_method":
                kind = "method"; break;
            case "class":
                kind = "class"; break;
            case "module":
                kind = "module"; break;
            default:
                return false;
        }
        name = node.GetChildForField("name")?.Text ?? string.Empty;
        return name.Length > 0;
    }
}

/// <summary>
/// PHP's definitions end in <c>_declaration</c>/<c>_definition</c> with a <c>name</c> field, so
/// the generic <c>TryGetDefinition</c> handles them. Calls, however, are <c>*_call_expression</c>
/// (not matched by the generic <c>*_call</c> rule), and PHP's identifier node is <c>name</c>
/// rather than <c>identifier</c> — both need overriding.
/// </summary>
public sealed class PhpNodeProfile : GenericNodeProfile
{
    public override string LanguageId => "php";

    public override bool IsIdentifier(string t) => t == "name" || base.IsIdentifier(t);

    public override bool TryGetCall(Node node, out string calleeName)
    {
        calleeName = string.Empty;
        switch (node.Type)
        {
            case "function_call_expression":
                // The callee is the `function` field (a name / qualified_name); the leaf is
                // its rightmost name segment. Fall back to the first child for safety.
                var fn = node.GetChildForField("function") ?? node.NamedChildren.FirstOrDefault();
                calleeName = fn is not null ? LastName(fn) ?? fn.Text ?? string.Empty : string.Empty;
                break;
            case "member_call_expression":
            case "nullsafe_member_call_expression":
            case "scoped_call_expression":
                calleeName = node.GetChildForField("name")?.Text ?? string.Empty;
                break;
            default:
                return false;
        }
        return calleeName.Length > 0;
    }

    /// <summary>Rightmost <c>name</c>-typed descendant (PHP's identifier), e.g. <c>foo</c> in <c>App\foo</c>.</summary>
    private static string? LastName(Node node)
    {
        if (node.Type == "name") return node.Text;
        string? found = null;
        foreach (var c in node.NamedChildren)
        {
            var r = LastName(c);
            if (r is not null) found = r;
        }
        return found;
    }
}

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
