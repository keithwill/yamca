using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Handles both <c>typescript</c> and <c>tsx</c> grammars — they expose the same
/// container/member node kinds.
/// </summary>
public sealed class TypeScriptSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "typescript";

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
            var target = (child.Type == "export_statement")
                ? child.NamedChildren.FirstOrDefault() ?? child
                : child;

            switch (target.Type)
            {
                case "class_declaration":
                case "abstract_class_declaration":
                    EmitContainer(target, "class", depth, sink, source);
                    break;
                case "interface_declaration":
                    EmitContainer(target, "interface", depth, sink, source);
                    break;
                case "enum_declaration":
                    EmitContainer(target, "enum", depth, sink, source);
                    break;
                case "module":
                case "internal_module":
                    EmitContainer(target, "namespace", depth, sink, source);
                    break;

                case "type_alias_declaration":
                    var taName = target.NameOrAnonymous();
                    sink.Add(Symbol.From("type", $"type {taName}", target.NameOrEmpty(), target, depth));
                    break;

                case "function_declaration":
                case "generator_function_declaration":
                    var fb = target.GetChildForField("body");
                    sink.Add(Symbol.From("function", SignatureFormatter.SliceHeader(target, fb, source),
                        target.NameOrEmpty(), target, depth));
                    break;

                case "method_definition":
                case "method_signature":
                case "abstract_method_signature":
                    var mb = target.GetChildForField("body");
                    sink.Add(Symbol.From("method", SignatureFormatter.SliceHeader(target, mb, source),
                        target.NameOrEmpty(), target, depth));
                    break;

                case "public_field_definition":
                    sink.Add(Symbol.From("field", SignatureFormatter.SliceHeader(target, null, source),
                        target.NameOrEmpty(), target, depth));
                    break;

                case "program":
                case "class_body":
                case "interface_body":
                case "object_type":
                case "statement_block":
                    Walk(target, depth, sink, source);
                    break;
            }
        }
    }

    private static void EmitContainer(Node node, string kind, int depth, List<Symbol> sink, string source)
    {
        var name = node.NameOrAnonymous();
        sink.Add(Symbol.From(kind, $"{kind} {name}", node.NameOrEmpty(), node, depth));
        if (depth < SymbolDepth.MaxContainerDepth)
        {
            var body = node.GetChildForField("body");
            if (body is not null) Walk(body, depth + 1, sink, source);
        }
    }
}

/// <summary>TSX uses the <c>tsx</c> grammar with the same node kinds as <c>typescript</c>.</summary>
public sealed class TsxSymbolExtractor : ISymbolExtractor
{
    private readonly TypeScriptSymbolExtractor _inner = new();
    public string LanguageId => "tsx";
    public IEnumerable<Symbol> Extract(Node root, string source) => _inner.Extract(root, source);
}
