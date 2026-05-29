using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class CSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("c");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new CSymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void Function_NameFromDeclaratorChain()
    {
        const string src = """
            int add(int a, int b) {
                return a + b;
            }
            """;

        var symbols = Parse(src);
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("add"));
        Assert.That(fn.Display, Does.Contain("add(int a, int b)"));
        Assert.That(fn.Depth, Is.EqualTo(1));
        Assert.That(fn.Line, Is.EqualTo(1));
        Assert.That(fn.EndLine, Is.EqualTo(3));
    }

    [Test]
    public void PointerReturn_StillResolvesName()
    {
        const string src = "char *dup(const char *s) { return 0; }";
        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "func").Name, Is.EqualTo("dup"));
    }

    [Test]
    public void Prototype_InHeaderStyle_SurfacesAsFunc()
    {
        const string src = "int connect(const char *host, int port);";
        var symbols = Parse(src);
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("connect"));
    }

    [Test]
    public void Struct_WithFields_RecursesIntoMembers()
    {
        const string src = """
            struct Point {
                int x;
                int y;
            };
            """;

        var symbols = Parse(src);

        var st = symbols.Single(s => s.Kind == "struct");
        Assert.That(st.Name, Is.EqualTo("Point"));
        Assert.That(st.Depth, Is.EqualTo(1));

        var fields = symbols.Where(s => s.Kind == "field").Select(s => s.Name).ToList();
        Assert.That(fields, Is.EquivalentTo(new[] { "x", "y" }));
        Assert.That(symbols.Where(s => s.Kind == "field").Select(s => s.Depth), Is.All.EqualTo(2));
    }

    [Test]
    public void Enum_SurfacesConstants()
    {
        const string src = "enum Color { RED, GREEN, BLUE };";
        var symbols = Parse(src);

        Assert.That(symbols.Single(s => s.Kind == "enum").Name, Is.EqualTo("Color"));
        Assert.That(symbols.Where(s => s.Kind == "enum_value").Select(s => s.Name),
            Is.EquivalentTo(new[] { "RED", "GREEN", "BLUE" }));
    }

    [Test]
    public void Union_Extracted()
    {
        const string src = """
            union Value {
                int i;
                float f;
            };
            """;
        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "union").Name, Is.EqualTo("Value"));
        Assert.That(symbols.Where(s => s.Kind == "field").Select(s => s.Name),
            Is.EquivalentTo(new[] { "i", "f" }));
    }

    [Test]
    public void Typedef_NamedAlias()
    {
        const string src = "typedef unsigned long ulong_alias;";
        var symbols = Parse(src);
        var td = symbols.Single(s => s.Kind == "typedef");
        Assert.That(td.Name, Is.EqualTo("ulong_alias"));
    }

    [Test]
    public void Typedef_TSuffixName_IsLexedAsPrimitiveButStillResolves()
    {
        // tree-sitter-c lexes `*_t` names as primitive_type, leaving no declarator field.
        const string src = "typedef struct foo foo_t;";
        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "typedef").Name, Is.EqualTo("foo_t"));
    }

    [Test]
    public void TypedefAnonymousStruct_SurfacesNameAndFields()
    {
        const string src = """
            typedef struct {
                int width;
                int height;
            } Size;
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "typedef").Name, Is.EqualTo("Size"));
        Assert.That(symbols.Where(s => s.Kind == "field").Select(s => s.Name),
            Is.EquivalentTo(new[] { "width", "height" }));
    }

    [Test]
    public void Macros_ObjectAndFunctionLike()
    {
        const string src = """
            #define MAX 100
            #define SQUARE(x) ((x) * (x))
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Where(s => s.Kind == "macro").Select(s => s.Name),
            Is.EquivalentTo(new[] { "MAX", "SQUARE" }));
    }

    [Test]
    public void PlainGlobal_IsNotSurfaced()
    {
        // Bare global variables are orientation noise — only functions and types surface.
        const string src = """
            int global_counter = 0;
            int compute(void) { return global_counter; }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Any(s => s.Kind == "func" && s.Name == "compute"), Is.True);
        Assert.That(symbols.Any(s => s.Name == "global_counter"), Is.False);
    }
}
