using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a Java syntax tree (tree-sitter-java grammar) and pulls out the package, types
/// and members an LLM would care about for orientation. Walks via named children rather
/// than a tree-sitter query so depth tracking stays straightforward. Mirrors
/// <see cref="CSharpSymbolExtractor"/> — the two grammars share the same shape (a leading
/// scope declaration, then nested type/member declarations with <c>name</c> and <c>body</c>
/// fields), so this is the template the other JVM/C-family extractors should follow.
/// </summary>
public sealed class JavaSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "java";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, sink, source);
        return sink;
    }

    private static void Walk(Node node, int depth, List<Symbol> sink, string source)
    {
        // A `package x.y.z;` declaration has no body — everything that follows it in the
        // file is logically inside that package. Mirror the C# file-scoped-namespace
        // handling: emit it, then bump currentDepth for the rest of this scope.
        var currentDepth = depth;

        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "package_declaration")
            {
                var pkg = PackageName(child);
                sink.Add(Symbol.From("package", $"package {pkg}", pkg, child, currentDepth));
                if (currentDepth < SymbolDepth.MaxContainerDepth) currentDepth++;
                continue;
            }

            if (TryContainer(child, out var containerKind))
            {
                var name = NameOrAnonymous(child);
                sink.Add(Symbol.From(containerKind, $"{containerKind} {name}", BareName(child), child, currentDepth));
                if (currentDepth < SymbolDepth.MaxContainerDepth)
                {
                    var body = child.GetChildForField("body");
                    if (body is not null)
                        Walk(body, currentDepth + 1, sink, source);
                }
                continue;
            }

            if (TryMember(child, out var memberKind))
            {
                sink.Add(Symbol.From(memberKind, BuildMemberDisplay(memberKind, child, source), MemberName(memberKind, child), child, currentDepth));
                continue;
            }

            // Descend through structural wrappers (the program root, type bodies, and the
            // enum_body_declarations block that holds an enum's methods) without affecting
            // depth — the container that owns the body already bumped it.
            if (IsStructuralWrapper(child.Type))
                Walk(child, currentDepth, sink, source);
        }
    }

    private static bool TryContainer(Node node, out string kind)
    {
        switch (node.Type)
        {
            case "class_declaration":
                kind = "class"; return true;
            case "interface_declaration":
                kind = "interface"; return true;
            case "enum_declaration":
                kind = "enum"; return true;
            case "record_declaration":
                kind = "record"; return true;
            case "annotation_type_declaration":
                kind = "@interface"; return true;
            default:
                kind = string.Empty; return false;
        }
    }

    private static bool TryMember(Node node, out string kind)
    {
        switch (node.Type)
        {
            case "method_declaration":      kind = "method"; return true;
            case "constructor_declaration": kind = "ctor"; return true;
            case "field_declaration":       kind = "field"; return true;
            case "enum_constant":           kind = "enum_value"; return true;
            // Annotation members read as a `name()` element with an optional default.
            case "annotation_type_element_declaration": kind = "element"; return true;
            default:                        kind = string.Empty; return false;
        }
    }

    private static bool IsStructuralWrapper(string type) => type switch
    {
        "program" => true,
        "class_body" => true,
        "interface_body" => true,
        "enum_body" => true,
        "enum_body_declarations" => true,
        "annotation_type_body" => true,
        _ => false,
    };

    private static string NameOrAnonymous(Node node)
    {
        var name = node.GetChildForField("name");
        return name?.Text ?? "<anonymous>";
    }

    /// <summary>Bare leaf name for lookup (empty when the node has no <c>name</c> field).</summary>
    private static string BareName(Node node) => node.GetChildForField("name")?.Text ?? string.Empty;

    /// <summary>
    /// A package declaration carries its name as a child <c>identifier</c> /
    /// <c>scoped_identifier</c> rather than a <c>name</c> field. Take the first such child.
    /// </summary>
    private static string PackageName(Node node)
    {
        foreach (var child in node.NamedChildren)
            if (child.Type is "identifier" or "scoped_identifier")
                return child.Text ?? "<anonymous>";
        return "<anonymous>";
    }

    private static string MemberName(string kind, Node node)
    {
        var name = node.GetChildForField("name");
        if (name is not null) return name.Text ?? string.Empty;

        // Fields declare their name on a nested variable_declarator (`int foo, bar;`)
        // rather than a `name` field. Take the first declarator.
        if (kind == "field")
        {
            var declarator = FirstDescendant(node, "variable_declarator");
            return declarator?.GetChildForField("name")?.Text
                ?? declarator?.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text
                ?? string.Empty;
        }
        return string.Empty;
    }

    private static Node? FirstDescendant(Node node, string type)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == type) return child;
            var found = FirstDescendant(child, type);
            if (found is not null) return found;
        }
        return null;
    }

    private static string BuildMemberDisplay(string kind, Node node, string source)
    {
        // Methods/constructors slice up to the block body; fields have no body so the
        // whole declaration (sans initializer trivia, collapsed) becomes the signature.
        var body = node.GetChildForField("body")
                ?? node.GetChildForField("value");
        var header = SignatureFormatter.SliceHeader(node, body, source);
        return $"{kind} {header}";
    }
}
