using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a C syntax tree (tree-sitter-c grammar) and pulls out functions, aggregate types
/// (struct/union/enum), typedefs and macros. Two grammar quirks make this more involved than
/// the Java/C# extractors:
/// <list type="bullet">
/// <item>A function's name is buried in a <c>declarator</c> chain
/// (<c>function_declarator</c>, possibly wrapped in <c>pointer_declarator</c>), not a
/// <c>name</c> field — see <see cref="DeclaratorLeaf"/>.</item>
/// <item>Aggregates usually arrive wrapped: <c>struct Foo { … };</c> is a <c>declaration</c>
/// around a <c>struct_specifier</c>, and <c>typedef struct { … } Foo;</c> is a
/// <c>type_definition</c> around an anonymous one.</item>
/// </list>
/// Header files (<c>.h</c>) route here too, so function prototypes (a <c>declaration</c> whose
/// declarator is a <c>function_declarator</c>) surface as functions.
/// </summary>
public sealed class CSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "c";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, sink, source);
        return sink;
    }

    private static void Walk(Node node, int depth, List<Symbol> sink, string source)
    {
        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "function_definition":
                    EmitFunction(child, depth, sink, source);
                    break;

                case "declaration":
                    // A declaration is either a function prototype, an aggregate definition
                    // (struct/union/enum, possibly with a trailing variable), or a plain
                    // global. Prototypes and aggregates are worth surfacing; bare globals
                    // are noise for orientation, so they fall through and are skipped.
                    if (FindFunctionDeclarator(child) is not null)
                        EmitFunction(child, depth, sink, source);
                    else if (FindSpecifier(child) is { } spec)
                        EmitAggregate(spec, depth, sink, source);
                    break;

                case "struct_specifier":
                case "union_specifier":
                case "enum_specifier":
                    EmitAggregate(child, depth, sink, source);
                    break;

                case "type_definition":
                    EmitTypedef(child, depth, sink, source);
                    break;

                case "preproc_def":
                case "preproc_function_def":
                    var macro = child.NameOrAnonymous();
                    sink.Add(Symbol.From("macro", $"#define {macro}", macro, child, depth));
                    break;

                // Inside an aggregate body.
                case "field_declaration":
                    var fieldName = DeclaratorLeaf(child.GetChildForField("declarator"));
                    if (fieldName.Length > 0)
                        sink.Add(Symbol.From("field", $"field {SignatureFormatter.SliceHeader(child, null, source)}", fieldName, child, depth));
                    break;

                case "enumerator":
                    var enumName = child.NameOrEmpty();
                    if (enumName.Length > 0)
                        sink.Add(Symbol.From("enum_value", enumName, enumName, child, depth));
                    break;

                // Structural wrappers — descend without affecting depth.
                case "translation_unit":
                case "field_declaration_list":
                case "enumerator_list":
                case "linkage_specification":   // extern "C" { … }
                case "preproc_if":
                case "preproc_ifdef":
                    Walk(child, depth, sink, source);
                    break;
            }
        }
    }

    private static void EmitFunction(Node node, int depth, List<Symbol> sink, string source)
    {
        var name = DeclaratorLeaf(node.GetChildForField("declarator"));
        var body = node.GetChildForField("body"); // null for a prototype
        sink.Add(Symbol.From("func", $"func {SignatureFormatter.SliceHeader(node, body, source)}", name, node, depth));
    }

    private static void EmitAggregate(Node spec, int depth, List<Symbol> sink, string source)
    {
        var kind = spec.Type switch
        {
            "struct_specifier" => "struct",
            "union_specifier" => "union",
            "enum_specifier" => "enum",
            _ => "type",
        };
        var name = spec.NameOrAnonymous();
        sink.Add(Symbol.From(kind, $"{kind} {name}", spec.NameOrEmpty(), spec, depth));

        if (depth < SymbolDepth.MaxContainerDepth)
        {
            var body = spec.GetChildForField("body");
            if (body is not null) Walk(body, depth + 1, sink, source);
        }
    }

    private static void EmitTypedef(Node node, int depth, List<Symbol> sink, string source)
    {
        var name = TypedefName(node);
        sink.Add(Symbol.From("typedef", $"typedef {name}", name, node, depth));

        // `typedef struct { … } Foo;` — surface the anonymous aggregate's members under it.
        if (depth < SymbolDepth.MaxContainerDepth
            && node.GetChildForField("type") is { } type
            && type.Type is "struct_specifier" or "union_specifier" or "enum_specifier"
            && type.GetChildForField("body") is { } body)
        {
            Walk(body, depth + 1, sink, source);
        }
    }

    /// <summary>
    /// The alias a <c>type_definition</c> introduces. Normally the declarator leaf, but
    /// tree-sitter-c lexes any <c>*_t</c>-suffixed name as a <c>primitive_type</c> (a common
    /// C convention, e.g. <c>typedef struct foo foo_t;</c>), leaving no declarator field — in
    /// that case the alias is the trailing identifier-like child.
    /// </summary>
    internal static string TypedefName(Node typeDefinition)
    {
        var name = DeclaratorLeaf(typeDefinition.GetChildForField("declarator"));
        if (name.Length > 0) return name;

        var last = typeDefinition.NamedChildren.LastOrDefault();
        return last?.Type is "primitive_type" or "type_identifier" or "identifier"
            ? last.Text ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Follows the <c>declarator</c> field through pointer / array / function wrappers down to
    /// the leaf <c>identifier</c> / <c>field_identifier</c> / <c>type_identifier</c> and returns
    /// its text, or empty if there is none.
    /// </summary>
    internal static string DeclaratorLeaf(Node? declarator)
    {
        var cur = declarator;
        while (cur is { } node)
        {
            switch (node.Type)
            {
                case "identifier":
                case "field_identifier":
                case "type_identifier":
                    return node.Text ?? string.Empty;
                default:
                    // pointer_declarator / array_declarator / function_declarator / parenthesized.
                    cur = node.GetChildForField("declarator");
                    break;
            }
        }
        return string.Empty;
    }

    /// <summary>True-returning if <paramref name="declaration"/>'s declarator chain bottoms out
    /// in a <c>function_declarator</c> (i.e. it is a function prototype).</summary>
    private static Node? FindFunctionDeclarator(Node declaration)
    {
        var cur = declaration.GetChildForField("declarator");
        while (cur is { } node)
        {
            if (node.Type == "function_declarator") return node;
            cur = node.GetChildForField("declarator");
        }
        return null;
    }

    /// <summary>The struct/union/enum specifier in a declaration's <c>type</c> field, if any.</summary>
    private static Node? FindSpecifier(Node declaration)
    {
        var type = declaration.GetChildForField("type");
        return type?.Type is "struct_specifier" or "union_specifier" or "enum_specifier" ? type : null;
    }
}
