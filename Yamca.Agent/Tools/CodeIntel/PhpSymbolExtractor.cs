using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a PHP syntax tree (tree-sitter-php grammar). PHP maps cleanly onto the common
/// container/member shape — everything carries <c>name</c> and (usually) <c>body</c> fields,
/// and crucially methods (<c>method_declaration</c>) and free functions
/// (<c>function_definition</c>) are <em>distinct</em> node types, so no in-record flag is
/// needed. The one wrinkle is the namespace: <c>namespace X;</c> has no body and scopes the
/// rest of the file (handled like a C# file-scoped namespace), while <c>namespace X { … }</c>
/// has a body that is recursed into.
/// </summary>
public sealed class PhpSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "php";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, sink, source);
        return sink;
    }

    private static void Walk(Node node, int depth, List<Symbol> sink, string source)
    {
        var currentDepth = depth;

        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "namespace_definition":
                    var nsName = child.GetChildForField("name")?.Text ?? "<global>";
                    sink.Add(Symbol.From("namespace", $"namespace {nsName}", nsName, child, currentDepth));
                    var nsBody = child.GetChildForField("body");
                    if (nsBody is not null)
                    {
                        if (currentDepth < SymbolDepth.MaxContainerDepth)
                            Walk(nsBody, currentDepth + 1, sink, source);
                    }
                    else if (currentDepth < SymbolDepth.MaxContainerDepth)
                    {
                        // `namespace X;` — the rest of this scope is logically inside it.
                        currentDepth++;
                    }
                    break;

                case "class_declaration":
                    EmitContainer(child, "class", currentDepth, sink, source);
                    break;
                case "interface_declaration":
                    EmitContainer(child, "interface", currentDepth, sink, source);
                    break;
                case "trait_declaration":
                    EmitContainer(child, "trait", currentDepth, sink, source);
                    break;
                case "enum_declaration":
                    EmitContainer(child, "enum", currentDepth, sink, source);
                    break;

                case "function_definition":
                    EmitCallable(child, "func", currentDepth, sink, source);
                    break;
                case "method_declaration":
                    EmitCallable(child, "method", currentDepth, sink, source);
                    break;

                case "property_declaration":
                    EmitProperties(child, currentDepth, sink, source);
                    break;

                case "const_declaration":
                    foreach (var element in child.NamedChildren)
                    {
                        if (element.Type != "const_element") continue;
                        // const_element exposes its name as a plain child, not a `name` field.
                        var cname = element.NamedChildren.FirstOrDefault(c => c.Type == "name")?.Text ?? string.Empty;
                        if (cname.Length > 0)
                            sink.Add(Symbol.From("const", $"const {cname}", cname, element, currentDepth));
                    }
                    break;

                case "enum_case":
                    var caseName = child.NameOrEmpty();
                    if (caseName.Length > 0)
                        sink.Add(Symbol.From("enum_value", caseName, caseName, child, currentDepth));
                    break;

                // Structural wrappers — the members live one level inside the container's body.
                case "declaration_list":
                case "enum_declaration_list":
                    Walk(child, currentDepth, sink, source);
                    break;
            }
        }
    }

    private static void EmitContainer(Node node, string kind, int depth, List<Symbol> sink, string source)
    {
        var body = node.GetChildForField("body");
        sink.Add(Symbol.From(kind, SignatureFormatter.SliceHeader(node, body, source), node.NameOrEmpty(), node, depth));
        if (depth < SymbolDepth.MaxContainerDepth && body is not null)
            Walk(body, depth + 1, sink, source);
    }

    private static void EmitCallable(Node node, string kind, int depth, List<Symbol> sink, string source)
    {
        // body is absent for abstract/interface methods; the slice then runs to the `;`.
        var body = node.GetChildForField("body");
        sink.Add(Symbol.From(kind, SignatureFormatter.SliceHeader(node, body, source), node.NameOrEmpty(), node, depth));
    }

    private static void EmitProperties(Node declaration, int depth, List<Symbol> sink, string source)
    {
        var typeText = declaration.GetChildForField("type")?.Text;
        foreach (var element in declaration.NamedChildren)
        {
            if (element.Type != "property_element") continue;
            var varName = element.NameOrEmpty(); // e.g. "$id"
            if (varName.Length == 0) continue;
            var leaf = varName.TrimStart('$');
            var display = typeText is null ? $"field {varName}" : $"field {typeText} {varName}";
            sink.Add(Symbol.From("field", display, leaf, element, depth));
        }
    }
}
