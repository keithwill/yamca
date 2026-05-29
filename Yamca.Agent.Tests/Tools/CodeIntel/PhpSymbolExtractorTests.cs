using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class PhpSymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("php");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new PhpSymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void FileScopedNamespace_NestsFollowingDeclarations()
    {
        const string src = """
            <?php
            namespace App\Models;

            class User {
                public function name(): string { return "x"; }
            }
            """;

        var symbols = Parse(src);

        var ns = symbols.Single(s => s.Kind == "namespace");
        Assert.That(ns.Name, Is.EqualTo(@"App\Models"));
        Assert.That(ns.Depth, Is.EqualTo(1));

        var cls = symbols.Single(s => s.Kind == "class");
        Assert.That(cls.Name, Is.EqualTo("User"));
        Assert.That(cls.Depth, Is.EqualTo(2));

        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("name"));
        Assert.That(m.Display, Does.Contain("function name(): string"));
        Assert.That(m.Depth, Is.EqualTo(3));
    }

    [Test]
    public void BracedNamespace_RecursesIntoBody()
    {
        const string src = """
            <?php
            namespace App {
                function helper(): void {}
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "namespace").Depth, Is.EqualTo(1));
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("helper"));
        Assert.That(fn.Depth, Is.EqualTo(2));
    }

    [Test]
    public void ClassWithExtendsAndImplements_DisplayShowsClause()
    {
        const string src = """
            <?php
            class User extends Base implements Nameable {
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "class").Display,
            Is.EqualTo("class User extends Base implements Nameable"));
    }

    [Test]
    public void InterfaceAndTrait_Extracted()
    {
        const string src = """
            <?php
            interface Nameable {
                public function name(): string;
            }
            trait Greets {
                public function greet(): string { return "hi"; }
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "interface").Name, Is.EqualTo("Nameable"));
        Assert.That(symbols.Single(s => s.Kind == "trait").Name, Is.EqualTo("Greets"));
        // The interface's abstract method (no body) still surfaces, signature through the `;`.
        Assert.That(symbols.Where(s => s.Kind == "method").Select(s => s.Name),
            Is.EquivalentTo(new[] { "name", "greet" }));
    }

    [Test]
    public void PropertiesAndConstants_Extracted()
    {
        const string src = """
            <?php
            class User {
                public const ROLE = "user";
                private int $id;
                public string $name;
            }
            """;

        var symbols = Parse(src);

        Assert.That(symbols.Single(s => s.Kind == "const").Name, Is.EqualTo("ROLE"));

        var fields = symbols.Where(s => s.Kind == "field").ToList();
        Assert.That(fields.Select(s => s.Name), Is.EquivalentTo(new[] { "id", "name" }));
        Assert.That(fields.Single(s => s.Name == "id").Display, Is.EqualTo("field int $id"));
    }

    [Test]
    public void MultiplePropertiesInOneDeclaration_EachSurface()
    {
        const string src = """
            <?php
            class Box {
                private $a, $b;
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Where(s => s.Kind == "field").Select(s => s.Name),
            Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public void Enum_SurfacesCases()
    {
        const string src = """
            <?php
            enum Suit: string {
                case Hearts = 'H';
                case Spades = 'S';
            }
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "enum").Display, Does.Contain("enum Suit: string"));
        Assert.That(symbols.Where(s => s.Kind == "enum_value").Select(s => s.Name),
            Is.EquivalentTo(new[] { "Hearts", "Spades" }));
    }

    [Test]
    public void TopLevelFunctionAndConst_AtDepth1()
    {
        const string src = """
            <?php
            const VERSION = "1.0";
            function boot(): void {}
            """;

        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "const").Name, Is.EqualTo("VERSION"));
        var fn = symbols.Single(s => s.Kind == "func");
        Assert.That(fn.Name, Is.EqualTo("boot"));
        Assert.That(fn.Depth, Is.EqualTo(1));
    }
}
