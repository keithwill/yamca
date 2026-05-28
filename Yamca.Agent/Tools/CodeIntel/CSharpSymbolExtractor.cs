using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a C# syntax tree (tree-sitter-c-sharp grammar) and pulls out the namespaces,
/// types and members an LLM would care about for orientation. Walks via named children
/// rather than a tree-sitter query so depth tracking stays straightforward.
/// </summary>
public sealed class CSharpSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "c-sharp";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, sink, source);
        return sink;
    }

    private static void Walk(Node node, int depth, List<Symbol> sink, string source)
    {
        // File-scoped namespaces (`namespace X;`) have no body field — instead, every
        // sibling that follows them in the compilation_unit is logically nested inside
        // the namespace. Track that by bumping currentDepth for the rest of this scope.
        var currentDepth = depth;

        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "file_scoped_namespace_declaration")
            {
                var nsName = NameOrAnonymous(child);
                sink.Add(new Symbol("namespace", $"namespace {nsName}", child.StartPosition.Row + 1, currentDepth));
                if (currentDepth < 3) currentDepth++;
                continue;
            }

            if (TryContainer(child, out var containerKind))
            {
                var name = NameOrAnonymous(child);
                sink.Add(new Symbol(containerKind, $"{containerKind} {name}", child.StartPosition.Row + 1, currentDepth));
                if (currentDepth < 3)
                {
                    var body = child.GetChildForField("body");
                    if (body is not null)
                        Walk(body, currentDepth + 1, sink, source);
                }
                continue;
            }

            if (TryMember(child, out var memberKind))
            {
                sink.Add(new Symbol(memberKind, BuildMemberDisplay(memberKind, child, source), child.StartPosition.Row + 1, currentDepth));
                continue;
            }

            // Descend through structural wrappers (compilation_unit, top-level statements,
            // declaration_list under namespaces / types) without affecting depth.
            if (IsStructuralWrapper(child.Type))
                Walk(child, currentDepth, sink, source);
        }
    }

    private static bool TryContainer(Node node, out string kind)
    {
        switch (node.Type)
        {
            case "namespace_declaration":
            case "file_scoped_namespace_declaration":
                kind = "namespace"; return true;
            case "class_declaration":
                kind = "class"; return true;
            case "struct_declaration":
                kind = "struct"; return true;
            case "interface_declaration":
                kind = "interface"; return true;
            case "enum_declaration":
                kind = "enum"; return true;
            case "record_declaration":
            case "record_struct_declaration":
                kind = "record"; return true;
            default:
                kind = string.Empty; return false;
        }
    }

    private static bool TryMember(Node node, out string kind)
    {
        switch (node.Type)
        {
            case "method_declaration":           kind = "method"; return true;
            case "constructor_declaration":      kind = "ctor"; return true;
            case "destructor_declaration":       kind = "dtor"; return true;
            case "property_declaration":         kind = "property"; return true;
            case "indexer_declaration":          kind = "indexer"; return true;
            case "field_declaration":            kind = "field"; return true;
            case "event_declaration":            kind = "event"; return true;
            case "event_field_declaration":      kind = "event"; return true;
            case "delegate_declaration":         kind = "delegate"; return true;
            case "operator_declaration":         kind = "operator"; return true;
            case "conversion_operator_declaration": kind = "operator"; return true;
            case "enum_member_declaration":      kind = "enum_value"; return true;
            default:                              kind = string.Empty; return false;
        }
    }

    private static bool IsStructuralWrapper(string type) => type switch
    {
        "compilation_unit" => true,
        "declaration_list" => true,
        "global_statement" => true,
        _ => false,
    };

    private static string NameOrAnonymous(Node node)
    {
        var name = node.GetChildForField("name");
        return name?.Text ?? "<anonymous>";
    }

    private static string BuildMemberDisplay(string kind, Node node, string source)
    {
        var body = node.GetChildForField("body")
                ?? node.GetChildForField("accessors")
                ?? node.GetChildForField("value");
        var header = SignatureFormatter.SliceHeader(node, body, source);
        return $"{kind} {header}";
    }
}
