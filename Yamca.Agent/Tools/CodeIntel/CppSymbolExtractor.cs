using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a C++ syntax tree (tree-sitter-cpp grammar). Extends what
/// <see cref="CSymbolExtractor"/> does for C with the C++-only surface: namespaces, classes,
/// templates, scoped enums, and out-of-line member definitions. Three grammar facts drive the
/// shape of this walker:
/// <list type="bullet">
/// <item><c>template &lt;…&gt;</c> is a <c>template_declaration</c> wrapper around the real
/// class/function — descended through transparently.</item>
/// <item>The same node (<c>function_definition</c> / <c>declaration</c> / <c>field_declaration</c>)
/// is a <em>method</em> inside a class body but a <em>free function</em> at namespace scope, so
/// the walk threads an <c>inRecord</c> flag.</item>
/// <item>A name can be a <c>destructor_name</c> (<c>~Widget</c>) or a nested
/// <c>qualified_identifier</c> (<c>acme::Widget::value</c> for an out-of-line definition) —
/// see <see cref="DeclaratorLeaf"/>.</item>
/// </list>
/// </summary>
public sealed class CppSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "cpp";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, inRecord: false, sink, source);
        return sink;
    }

    private static void Walk(Node node, int depth, bool inRecord, List<Symbol> sink, string source)
    {
        foreach (var child in node.NamedChildren)
            Dispatch(child, depth, inRecord, sink, source);
    }

    private static void Dispatch(Node child, int depth, bool inRecord, List<Symbol> sink, string source)
    {
        switch (child.Type)
        {
            case "namespace_definition":
                EmitContainer(child, "namespace", depth, inRecord: false, sink, source);
                break;

            case "class_specifier":
            case "struct_specifier":
            case "union_specifier":
                EmitContainer(child, child.Type[..child.Type.IndexOf('_')], depth, inRecord: true, sink, source);
                break;

            case "enum_specifier":
                EmitContainer(child, "enum", depth, inRecord: false, sink, source);
                break;

            case "function_definition":
                EmitFunction(child, depth, inRecord, sink, source);
                break;

            case "declaration":
                // Prototype (function_declarator) → func/method; a wrapped aggregate →
                // treat as a container; otherwise a plain variable — skipped as noise.
                if (FindFunctionDeclarator(child) is not null)
                    EmitFunction(child, depth, inRecord, sink, source);
                else if (FindSpecifier(child) is { } spec)
                    Dispatch(spec, depth, inRecord, sink, source);
                break;

            case "field_declaration":
                if (FindFunctionDeclarator(child) is not null)
                    sink.Add(Symbol.From("method", $"method {SignatureFormatter.SliceHeader(child, null, source)}", DeclaratorLeaf(child.GetChildForField("declarator")), child, depth));
                else
                {
                    var fname = DeclaratorLeaf(child.GetChildForField("declarator"));
                    if (fname.Length > 0)
                        sink.Add(Symbol.From("field", $"field {SignatureFormatter.SliceHeader(child, null, source)}", fname, child, depth));
                }
                break;

            case "type_definition":
                var tname = CSymbolExtractor.TypedefName(child);
                sink.Add(Symbol.From("typedef", $"typedef {tname}", tname, child, depth));
                break;

            case "preproc_def":
            case "preproc_function_def":
                var macro = child.GetChildForField("name")?.Text ?? "<anonymous>";
                sink.Add(Symbol.From("macro", $"#define {macro}", macro, child, depth));
                break;

            case "enumerator":
                var en = child.GetChildForField("name")?.Text ?? string.Empty;
                if (en.Length > 0) sink.Add(Symbol.From("enum_value", en, en, child, depth));
                break;

            // Wrappers — descend without affecting depth, preserving record context. A
            // template_declaration carries its parameter list plus the real class/function;
            // the parameter list has no handled node types, so iterating its children is safe.
            case "template_declaration":
            case "translation_unit":
            case "declaration_list":
            case "field_declaration_list":
            case "enumerator_list":
            case "linkage_specification":
                Walk(child, depth, inRecord, sink, source);
                break;
        }
    }

    private static void EmitContainer(Node spec, string kind, int depth, bool inRecord, List<Symbol> sink, string source)
    {
        var body = spec.GetChildForField("body");
        sink.Add(Symbol.From(kind, SignatureFormatter.SliceHeader(spec, body, source), spec.GetChildForField("name")?.Text ?? string.Empty, spec, depth));
        if (depth < SymbolDepth.MaxContainerDepth && body is not null)
            Walk(body, depth + 1, inRecord, sink, source);
    }

    private static void EmitFunction(Node node, int depth, bool inRecord, List<Symbol> sink, string source)
    {
        var name = DeclaratorLeaf(node.GetChildForField("declarator"));
        var body = node.GetChildForField("body"); // null for a prototype
        var kind = inRecord ? "method" : "func";
        sink.Add(Symbol.From(kind, $"{kind} {SignatureFormatter.SliceHeader(node, body, source)}", name, node, depth));
    }

    /// <summary>
    /// Follows the <c>declarator</c> field through pointer / reference / array / function
    /// wrappers to the leaf name. Handles two C++ shapes the C walker doesn't: a
    /// <c>destructor_name</c> (<c>~Widget</c>) and a <c>qualified_identifier</c>
    /// (<c>acme::Widget::value</c>) whose rightmost segment is the member name.
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
                case "destructor_name":     // ~Widget
                case "operator_name":       // operator==
                    return node.Text ?? string.Empty;
                case "qualified_identifier":
                    // Descend the `name` field to the rightmost segment (Foo::Bar::baz → baz).
                    cur = node.GetChildForField("name");
                    break;
                default:
                    cur = node.GetChildForField("declarator");
                    break;
            }
        }
        return string.Empty;
    }

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

    private static Node? FindSpecifier(Node declaration)
    {
        var type = declaration.GetChildForField("type");
        return type?.Type is "class_specifier" or "struct_specifier" or "union_specifier" or "enum_specifier"
            ? type
            : null;
    }
}
