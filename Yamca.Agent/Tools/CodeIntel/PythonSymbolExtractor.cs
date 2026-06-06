using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

public sealed class PythonSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "python";

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
            // Decorators wrap the actual def. Unwrap.
            var target = child.Type == "decorated_definition"
                ? child.GetChildForField("definition") ?? child
                : child;

            switch (target.Type)
            {
                case "class_definition":
                    var className = target.NameOrAnonymous();
                    sink.Add(Symbol.From("class", $"class {className}", target.NameOrEmpty(), target, depth));
                    if (depth < SymbolDepth.MaxContainerDepth)
                    {
                        var body = target.GetChildForField("body");
                        if (body is not null) Walk(body, depth + 1, sink, source);
                    }
                    break;

                case "function_definition":
                    var body2 = target.GetChildForField("body");
                    var sig = SignatureFormatter.SliceHeader(target, body2, source);
                    sink.Add(Symbol.From("def", sig, target.NameOrEmpty(), target, depth));
                    break;

                case "module":
                case "block":
                    Walk(target, depth, sink, source);
                    break;
            }
        }
    }
}
