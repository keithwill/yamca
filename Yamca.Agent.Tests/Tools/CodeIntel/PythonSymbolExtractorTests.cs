using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class PythonSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("python");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        return new PythonSymbolExtractor().Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void ClassAndDefs_AreExtracted()
    {
        const string src = """
            class Greeter:
                def hello(self, name):
                    return f"Hi {name}"

                async def fetch(self, url):
                    return None

            def top_level(x: int) -> str:
                return str(x)
            """;

        var symbols = Parse(src);

        Assert.That(symbols.Any(s => s.Kind == "class" && s.Display == "class Greeter"), Is.True);
        Assert.That(symbols.Any(s => s.Kind == "def" && s.Display.Contains("hello")), Is.True);
        Assert.That(symbols.Any(s => s.Kind == "def" && s.Display.Contains("fetch")), Is.True);
        Assert.That(symbols.Any(s => s.Display.Contains("top_level")), Is.True);
    }

    [Test]
    public void DecoratedFunction_IsUnwrapped()
    {
        const string src = """
            @staticmethod
            def utility(x):
                return x
            """;
        var symbols = Parse(src);
        Assert.That(symbols.Any(s => s.Display.Contains("utility")), Is.True);
    }
}
