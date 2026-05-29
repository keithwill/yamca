using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class CppSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("cpp");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new CppSymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void Namespace_Class_Members_Nest()
    {
        const string src = """
            namespace acme {
              class Widget : public Base {
              public:
                Widget(int n);
                ~Widget();
                int value() const;
              private:
                int n_;
              };
            }
            """;

        var symbols = Parse(src);

        var ns = symbols.Single(s => s.Kind == "namespace");
        Assert.That(ns.Name, Is.EqualTo("acme"));
        Assert.That(ns.Depth, Is.EqualTo(1));

        var cls = symbols.Single(s => s.Kind == "class");
        Assert.That(cls.Name, Is.EqualTo("Widget"));
        Assert.That(cls.Display, Is.EqualTo("class Widget : public Base"));
        Assert.That(cls.Depth, Is.EqualTo(2));

        // Members are methods (not free functions) because they are inside a class body.
        var methods = symbols.Where(s => s.Kind == "method").Select(s => s.Name).ToList();
        Assert.That(methods, Has.Member("Widget"));   // constructor
        Assert.That(methods, Has.Member("~Widget"));  // destructor
        Assert.That(methods, Has.Member("value"));
        Assert.That(symbols.Where(s => s.Kind == "method").Select(s => s.Depth), Is.All.EqualTo(3));

        var field = symbols.Single(s => s.Kind == "field");
        Assert.That(field.Name, Is.EqualTo("n_"));
    }

    [Test]
    public void FreeFunction_AtNamespaceScope_IsFunc()
    {
        const string src = """
            namespace util {
              int add(int a, int b) { return a + b; }
            }
            """;

        var symbols = Parse(src);
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("add"));
        Assert.That(fn.Display, Does.Contain("add(int a, int b)"));
    }

    [Test]
    public void TopLevelFunction_IsFunc()
    {
        const string src = "int main() { return 0; }";
        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "func").Name, Is.EqualTo("main"));
    }

    [Test]
    public void TemplateClassAndFunction_Unwrapped()
    {
        const string src = """
            template <typename T>
            class Box {
            public:
              T get() const;
            };

            template <typename T>
            T identity(T x) { return x; }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "class").Name, Is.EqualTo("Box"));
        Assert.That(symbols.Single(s => s.Kind == "method").Name, Is.EqualTo("get"));
        Assert.That(symbols.Single(s => s.Kind == "func").Name, Is.EqualTo("identity"));
    }

    [Test]
    public void OutOfLineDefinition_ResolvesLeafName()
    {
        const string src = "int acme::Widget::value() const { return n_; }";
        var symbols = Parse(src);
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("value"));
        // The qualified path is preserved in the display for the reader.
        Assert.That(fn.Display, Does.Contain("acme::Widget::value"));
    }

    [Test]
    public void ScopedEnum_SurfacesConstants()
    {
        const string src = "enum class Color { Red, Green, Blue };";
        var symbols = Parse(src);
        var en = symbols.Single(s => s.Kind == "enum");
        Assert.That(en.Name, Is.EqualTo("Color"));
        Assert.That(en.Display, Does.Contain("enum class Color"));
        Assert.That(symbols.Where(s => s.Kind == "enum_value").Select(s => s.Name),
            Is.EquivalentTo(new[] { "Red", "Green", "Blue" }));
    }

    [Test]
    public void Struct_AtNamespaceScope_Extracted()
    {
        const string src = """
            struct Point {
              int x;
              int y;
            };
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "struct").Name, Is.EqualTo("Point"));
        Assert.That(symbols.Where(s => s.Kind == "field").Select(s => s.Name),
            Is.EquivalentTo(new[] { "x", "y" }));
    }

    [Test]
    public void OperatorOverload_ResolvesName()
    {
        const string src = """
            struct Vec {
              Vec operator+(const Vec& o) const;
            };
            """;

        var symbols = Parse(src);
        var op = symbols.Single(s => s.Kind == "method");
        Assert.That(op.Name, Is.EqualTo("operator+"));
    }
}
