using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Walks a Ruby syntax tree (tree-sitter-ruby grammar) and pulls out modules, classes,
/// methods (instance and singleton), and top-level/class constants. Ruby's containers wrap
/// their members in a <c>body_statement</c> node, which this descends into at the container's
/// depth + 1. Method names live in a <c>name</c> field, but an empty <c>def foo; end</c> has
/// no <c>body</c> field — see <see cref="MethodDisplay"/>.
/// </summary>
public sealed class RubySymbolExtractor : ISymbolExtractor
{
    public string LanguageId => "ruby";

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
                case "module":
                case "class":
                    var kind = child.Type;
                    var body = child.GetChildForField("body");
                    sink.Add(Symbol.From(kind, SignatureFormatter.SliceHeader(child, body, source), NameText(child), child, depth));
                    if (depth < SymbolDepth.MaxContainerDepth && body is not null)
                        Walk(body, depth + 1, sink, source);
                    break;

                case "method":
                case "singleton_method":
                    sink.Add(Symbol.From("method", MethodDisplay(child, source), NameText(child), child, depth));
                    break;

                case "singleton_class":
                    // `class << self` — surface its methods as peers of the enclosing scope's
                    // members rather than introducing a synthetic container.
                    if (depth < SymbolDepth.MaxContainerDepth && child.GetChildForField("body") is { } scBody)
                        Walk(scBody, depth, sink, source);
                    break;

                case "assignment":
                    // Constant definitions (`FOO = …`, `Foo::Bar = …`) are worth surfacing;
                    // other assignments (locals, ivars) are not.
                    var left = child.GetChildForField("left");
                    if (left?.Type is "constant" or "scope_resolution")
                        sink.Add(Symbol.From("const", $"const {left.Text}", left.Text ?? string.Empty, child, depth));
                    break;

                // body_statement is the members wrapper inside a module/class/method; when a
                // container hands us one we iterate it directly, so it only appears here when
                // nested (e.g. begin/ensure blocks). Descend without affecting depth.
                case "body_statement":
                    Walk(child, depth, sink, source);
                    break;
            }
        }
    }

    private static string NameText(Node node) => node.GetChildForField("name")?.Text ?? string.Empty;

    private static string MethodDisplay(Node node, string source)
    {
        // Slice up to the body (or the parameter list, for an abstract-ish bodyless def). The
        // slice naturally includes the `self.`/receiver prefix of a singleton_method.
        var boundary = node.GetChildForField("body") ?? node.GetChildForField("parameters");
        if (boundary is not null)
            return SignatureFormatter.SliceHeader(node, boundary, source);

        // `def foo; end` with no params and no body: build a minimal signature by hand so we
        // don't slice in the trailing `end`.
        var name = node.GetChildForField("name")?.Text ?? string.Empty;
        var receiver = node.GetChildForField("object")?.Text;
        return receiver is not null ? $"def {receiver}.{name}" : $"def {name}";
    }
}
