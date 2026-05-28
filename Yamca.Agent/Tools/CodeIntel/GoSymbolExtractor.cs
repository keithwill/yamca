using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

public sealed class GoSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "go";

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
                case "function_declaration":
                    sink.Add(new Symbol("func",
                        SignatureFormatter.SliceHeader(child, child.GetChildForField("body"), source),
                        child.StartPosition.Row + 1, depth));
                    break;

                case "method_declaration":
                    sink.Add(new Symbol("method",
                        SignatureFormatter.SliceHeader(child, child.GetChildForField("body"), source),
                        child.StartPosition.Row + 1, depth));
                    break;

                case "type_declaration":
                    foreach (var spec in child.NamedChildren)
                    {
                        if (spec.Type != "type_spec" && spec.Type != "type_alias") continue;
                        var name = spec.GetChildForField("name")?.Text ?? "<anonymous>";
                        var typeNode = spec.GetChildForField("type");
                        var kind = typeNode?.Type switch
                        {
                            "struct_type" => "struct",
                            "interface_type" => "interface",
                            _ => "type",
                        };
                        sink.Add(new Symbol(kind, $"{kind} {name}", spec.StartPosition.Row + 1, depth));
                    }
                    break;

                case "const_declaration":
                case "var_declaration":
                    foreach (var spec in child.NamedChildren)
                    {
                        if (spec.Type != "const_spec" && spec.Type != "var_spec") continue;
                        var nameNode = spec.GetChildForField("name") ?? spec.NamedChildren.FirstOrDefault();
                        if (nameNode is null) continue;
                        sink.Add(new Symbol(child.Type == "const_declaration" ? "const" : "var",
                            $"{(child.Type == "const_declaration" ? "const" : "var")} {nameNode.Text}",
                            spec.StartPosition.Row + 1, depth));
                    }
                    break;

                case "source_file":
                    Walk(child, depth, sink, source);
                    break;
            }
        }
    }
}
