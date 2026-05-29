using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class JavaSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("java");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new JavaSymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void PackageClassAndMethods_ExtractsContainerAndMembers()
    {
        const string src = """
            package com.acme;

            public class Foo {
                public void bar(int x) { }
                public String baz() { return "hi"; }
            }
            """;

        var symbols = Parse(src);

        Assert.That(symbols, Has.Count.EqualTo(4));
        Assert.That(symbols[0].Kind, Is.EqualTo("package"));
        Assert.That(symbols[0].Display, Is.EqualTo("package com.acme"));
        Assert.That(symbols[0].Depth, Is.EqualTo(1));

        Assert.That(symbols[1].Kind, Is.EqualTo("class"));
        Assert.That(symbols[1].Display, Is.EqualTo("class Foo"));
        Assert.That(symbols[1].Depth, Is.EqualTo(2));

        Assert.That(symbols[2].Kind, Is.EqualTo("method"));
        Assert.That(symbols[2].Display, Does.Contain("bar(int x)"));
        Assert.That(symbols[2].Depth, Is.EqualTo(3));

        Assert.That(symbols[3].Display, Does.Contain("baz()"));
    }

    [Test]
    public void TopLevelClass_NoPackage_StartsAtDepth1()
    {
        const string src = """
            public class Greeter {
                public String hello(String name) { return "Hi " + name; }
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
        const string src = "\n\nclass Foo {\n    void bar() { }\n}\n";
        // class is on line 3, method on line 4
        var symbols = Parse(src);
        Assert.That(symbols[0].Line, Is.EqualTo(3));
        Assert.That(symbols[1].Line, Is.EqualTo(4));
    }

    [Test]
    public void NameAndEndLine_ArePopulated()
    {
        const string src = """
            public class Foo {
                public void bar(int x) {
                    int y = x;
                }
            }
            """;

        var symbols = Parse(src);

        var cls = symbols.Single(s => s.Kind == "class");
        Assert.That(cls.Name, Is.EqualTo("Foo"));
        Assert.That(cls.Line, Is.EqualTo(1));
        Assert.That(cls.EndLine, Is.EqualTo(5));

        var method = symbols.Single(s => s.Kind == "method");
        Assert.That(method.Name, Is.EqualTo("bar"));
        Assert.That(method.Line, Is.EqualTo(2));
        Assert.That(method.EndLine, Is.EqualTo(4));
    }

    [Test]
    public void Field_NameExtractedFromDeclarator()
    {
        const string src = "class C { private int count; }";
        var symbols = Parse(src);
        var field = symbols.Single(s => s.Kind == "field");
        Assert.That(field.Name, Is.EqualTo("count"));
    }

    [Test]
    public void Constructor_ExtractedAsCtor()
    {
        const string src = """
            class Point {
                Point(int x, int y) { }
            }
            """;

        var symbols = Parse(src);
        var ctor = symbols.Single(s => s.Kind == "ctor");
        Assert.That(ctor.Name, Is.EqualTo("Point"));
        Assert.That(ctor.Display, Does.Contain("Point(int x, int y)"));
    }

    [Test]
    public void InterfaceEnumAndRecord_ExtractedAsContainers()
    {
        const string src = """
            interface IThing { void doIt(); }
            enum Color { RED, GREEN, BLUE }
            record Pair(int a, int b) { }
            """;

        var symbols = Parse(src);

        Assert.That(symbols.Where(s => s.Kind == "interface").Select(s => s.Display),
            Has.Member("interface IThing"));
        Assert.That(symbols.Where(s => s.Kind == "enum").Select(s => s.Display),
            Has.Member("enum Color"));
        Assert.That(symbols.Where(s => s.Kind == "record").Select(s => s.Display),
            Has.Member("record Pair"));

        // Enum constants surface as members under the enum.
        Assert.That(symbols.Where(s => s.Kind == "enum_value").Select(s => s.Name),
            Is.EquivalentTo(new[] { "RED", "GREEN", "BLUE" }));
    }

    [Test]
    public void NestedClass_NestsUnderOuter()
    {
        const string src = """
            class Outer {
                class Inner {
                    void tick() { }
                }
            }
            """;

        var symbols = Parse(src);

        var outer = symbols.Single(s => s.Name == "Outer");
        var inner = symbols.Single(s => s.Name == "Inner");
        Assert.That(outer.Depth, Is.EqualTo(1));
        Assert.That(inner.Depth, Is.EqualTo(2));
        Assert.That(symbols.Single(s => s.Name == "tick").Depth, Is.EqualTo(3));
    }

    [Test]
    public void ParseError_StillEmitsValidSymbols()
    {
        // Class is well-formed; one method body has stray characters. We only assert that
        // we still see the enclosing class and at least one method.
        const string src = """
            class Foo {
                void good() { int x = 1; }
                void bar() { ((( broken }
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Any(s => s.Kind == "class"), Is.True, "class symbol should still surface");
        Assert.That(symbols.Any(s => s.Kind == "method"), Is.True, "at least one method should surface");
    }
}
