using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

public sealed class JavaScriptSymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "javascript";

    public IEnumerable<Symbol> Extract(Node root, string source)
    {
        var sink = new List<Symbol>();
        Walk(root, depth: 1, sink, source);
        return sink;
    }

    internal static void Walk(Node node, int depth, List<Symbol> sink, string source)
    {
        foreach (var child in node.NamedChildren)
        {
            // Unwrap `export default class X` / `export class X`.
            var target = (child.Type == "export_statement")
                ? child.NamedChildren.FirstOrDefault() ?? child
                : child;

            switch (target.Type)
            {
                case "class_declaration":
                case "class":
                    var className = target.GetChildForField("name")?.Text ?? "<anonymous>";
                    sink.Add(new Symbol("class", $"class {className}", target.StartPosition.Row + 1, depth));
                    if (depth < 3)
                    {
                        var body = target.GetChildForField("body");
                        if (body is not null) Walk(body, depth + 1, sink, source);
                    }
                    break;

                case "function_declaration":
                case "generator_function_declaration":
                    var fb = target.GetChildForField("body");
                    sink.Add(new Symbol("function", SignatureFormatter.SliceHeader(target, fb, source),
                        target.StartPosition.Row + 1, depth));
                    break;

                case "method_definition":
                    var mb = target.GetChildForField("body");
                    sink.Add(new Symbol("method", SignatureFormatter.SliceHeader(target, mb, source),
                        target.StartPosition.Row + 1, depth));
                    break;

                case "program":
                case "class_body":
                    Walk(target, depth, sink, source);
                    break;
            }
        }
    }
}
