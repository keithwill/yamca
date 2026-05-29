using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

public sealed class RustSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "rust";

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
                case "mod_item":
                    var modName = child.GetChildForField("name")?.Text ?? "<anonymous>";
                    sink.Add(Symbol.From("mod", $"mod {modName}", child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    if (depth < SymbolDepth.MaxContainerDepth)
                    {
                        var body = child.GetChildForField("body");
                        if (body is not null) Walk(body, depth + 1, sink, source);
                    }
                    break;

                case "impl_item":
                    var implType = child.GetChildForField("type")?.Text ?? "?";
                    var implTrait = child.GetChildForField("trait")?.Text;
                    var implLabel = implTrait is null ? $"impl {implType}" : $"impl {implTrait} for {implType}";
                    sink.Add(Symbol.From("impl", implLabel, child.GetChildForField("type")?.Text ?? string.Empty, child, depth));
                    if (depth < SymbolDepth.MaxContainerDepth)
                    {
                        var body = child.GetChildForField("body");
                        if (body is not null) Walk(body, depth + 1, sink, source);
                    }
                    break;

                case "trait_item":
                    var traitName = child.GetChildForField("name")?.Text ?? "<anonymous>";
                    sink.Add(Symbol.From("trait", $"trait {traitName}", child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    if (depth < SymbolDepth.MaxContainerDepth)
                    {
                        var body = child.GetChildForField("body");
                        if (body is not null) Walk(body, depth + 1, sink, source);
                    }
                    break;

                case "struct_item":
                    sink.Add(Symbol.From("struct",
                        SignatureFormatter.SliceHeader(child, child.GetChildForField("body"), source),
                        child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    break;

                case "enum_item":
                    sink.Add(Symbol.From("enum",
                        SignatureFormatter.SliceHeader(child, child.GetChildForField("body"), source),
                        child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    break;

                case "function_item":
                case "function_signature_item":
                    sink.Add(Symbol.From("fn",
                        SignatureFormatter.SliceHeader(child, child.GetChildForField("body"), source),
                        child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    break;

                case "const_item":
                case "static_item":
                    sink.Add(Symbol.From(child.Type == "const_item" ? "const" : "static",
                        SignatureFormatter.SliceHeader(child, null, source),
                        child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    break;

                case "type_item":
                    sink.Add(Symbol.From("type",
                        SignatureFormatter.SliceHeader(child, null, source),
                        child.GetChildForField("name")?.Text ?? string.Empty, child, depth));
                    break;

                case "source_file":
                case "declaration_list":
                    Walk(child, depth, sink, source);
                    break;
            }
        }
    }
}
