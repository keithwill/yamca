using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class CSharpSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("c-sharp");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new CSharpSymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void ClassWithMethods_ExtractsContainerAndMembers()
    {
        const string src = """
            namespace Acme;

            public class Foo
            {
                public void Bar(int x) { }
                public string Baz() => "hi";
            }
            """;

        var symbols = Parse(src);

        Assert.That(symbols, Has.Count.EqualTo(4));
        Assert.That(symbols[0].Kind, Is.EqualTo("namespace"));
        Assert.That(symbols[0].Display, Is.EqualTo("namespace Acme"));
        Assert.That(symbols[0].Depth, Is.EqualTo(1));

        Assert.That(symbols[1].Kind, Is.EqualTo("class"));
        Assert.That(symbols[1].Display, Is.EqualTo("class Foo"));
        Assert.That(symbols[1].Depth, Is.EqualTo(2));

        Assert.That(symbols[2].Kind, Is.EqualTo("method"));
        Assert.That(symbols[2].Display, Does.Contain("Bar(int x)"));
        Assert.That(symbols[2].Depth, Is.EqualTo(3));

        Assert.That(symbols[3].Display, Does.Contain("Baz()"));
    }

    [Test]
    public void TopLevelClass_NoNamespace_StartsAtDepth1()
    {
        const string src = """
            public class Greeter
            {
                public string Hello(string name) => $"Hi {name}";
            }
            """;

        var symbols = Parse(src);

        Assert.That(symbols[0].Kind, Is.EqualTo("class"));
        Assert.That(symbols[0].Depth, Is.EqualTo(1));
        Assert.That(symbols[1].Kind, Is.EqualTo("method"));
        Assert.That(symbols[1].Depth, Is.EqualTo(2));
    }

    [Test]
    public void LineNumbers_AreOneIndexed()
    {
        const string src = "\n\nclass Foo {\n    void Bar() { }\n}\n";
        // class is on line 3, method on line 4
        var symbols = Parse(src);
        Assert.That(symbols[0].Line, Is.EqualTo(3));
        Assert.That(symbols[1].Line, Is.EqualTo(4));
    }

    [Test]
    public void ParseError_StillEmitsValidSymbols()
    {
        // Class is well-formed; one method body has stray characters. We only assert
        // that we still see the enclosing class and at least one method — tree-sitter
        // may or may not be able to disambiguate the broken sibling.
        const string src = """
            class Foo
            {
                void Good() { var x = 1; }
                void Bar() { ((( broken }
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Any(s => s.Kind == "class"), Is.True, "class symbol should still surface");
        Assert.That(symbols.Any(s => s.Kind == "method"), Is.True, "at least one method should surface");
    }

    [Test]
    public void RecordAndInterface_ExtractedAsContainers()
    {
        const string src = """
            public interface IThing { void Do(); }
            public record Point(int X, int Y);
            """;

        var symbols = Parse(src);

        Assert.That(symbols.Where(s => s.Kind == "interface").Select(s => s.Display),
            Has.Member("interface IThing"));
        Assert.That(symbols.Where(s => s.Kind == "record").Select(s => s.Display),
            Has.Member("record Point"));
    }
}
