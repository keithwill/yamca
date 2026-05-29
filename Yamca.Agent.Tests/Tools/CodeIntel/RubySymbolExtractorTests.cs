using NUnit.Framework;
using TreeSitter;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class RubySymbolExtractorTests
{
    private static List<Symbol> Parse(string source)
    {
        using var language = new Language("ruby");
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;
        var extractor = new RubySymbolExtractor();
        return extractor.Extract(tree.RootNode, source).ToList();
    }

    [Test]
    public void ModuleClassAndMethods_NestAndExtract()
    {
        const string src = """
            module Acme
              class Widget < Base
                def initialize(name)
                  @name = name
                end
              end
            end
            """;

        var symbols = Parse(src);

        var mod = symbols.Single(s => s.Kind == "module");
        Assert.That(mod.Name, Is.EqualTo("Acme"));
        Assert.That(mod.Display, Is.EqualTo("module Acme"));
        Assert.That(mod.Depth, Is.EqualTo(1));

        var cls = symbols.Single(s => s.Kind == "class");
        Assert.That(cls.Name, Is.EqualTo("Widget"));
        Assert.That(cls.Display, Is.EqualTo("class Widget < Base"));
        Assert.That(cls.Depth, Is.EqualTo(2));

        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("initialize"));
        Assert.That(m.Display, Does.Contain("initialize(name)"));
        Assert.That(m.Depth, Is.EqualTo(3));
    }

    [Test]
    public void TopLevelMethod_StartsAtDepth1()
    {
        const string src = """
            def greet(name)
              "hi #{name}"
            end
            """;

        var symbols = Parse(src);
        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("greet"));
        Assert.That(m.Depth, Is.EqualTo(1));
        Assert.That(m.Display, Does.Contain("greet(name)"));
    }

    [Test]
    public void SingletonMethod_KeepsReceiverInDisplay()
    {
        const string src = """
            class Factory
              def self.build(spec)
                new(spec)
              end
            end
            """;

        var symbols = Parse(src);
        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("build"));
        Assert.That(m.Display, Does.Contain("self.build(spec)"));
    }

    [Test]
    public void EmptyMethod_NoBody_RendersCleanSignature()
    {
        const string src = """
            class C
              def noop
              end
            end
            """;

        var symbols = Parse(src);
        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("noop"));
        // No trailing `end` slurped into the signature.
        Assert.That(m.Display, Is.EqualTo("def noop"));
    }

    [Test]
    public void Constant_SurfacedWithName()
    {
        const string src = """
            module Config
              MAX_RETRIES = 5
              x = 10
            end
            """;

        var symbols = Parse(src);
        var c = symbols.Single(s => s.Kind == "const");
        Assert.That(c.Name, Is.EqualTo("MAX_RETRIES"));
        Assert.That(c.Display, Is.EqualTo("const MAX_RETRIES"));
        // Local-variable assignments are not surfaced.
        Assert.That(symbols.Any(s => s.Name == "x"), Is.False);
    }

    [Test]
    public void LineNumbers_AreOneIndexed()
    {
        const string src = "\n\nclass Foo\n  def bar\n  end\nend\n";
        var symbols = Parse(src);
        Assert.That(symbols.Single(s => s.Kind == "class").Line, Is.EqualTo(3));
        Assert.That(symbols.Single(s => s.Kind == "method").Line, Is.EqualTo(4));
    }

    [Test]
    public void SingletonClass_MethodsSurfaceAsPeers()
    {
        const string src = """
            class Logger
              class << self
                def instance
                  @instance ||= new
                end
              end
            end
            """;

        var symbols = Parse(src);
        var m = symbols.Single(s => s.Kind == "method");
        Assert.That(m.Name, Is.EqualTo("instance"));
        // Recursed at the class's member depth (class is 1, members are 2).
        Assert.That(m.Depth, Is.EqualTo(2));
    }
}
