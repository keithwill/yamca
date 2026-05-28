using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeEditSymbolToolTests
{
    private TempWorkspace _ws = null!;
    private CodeEditSymbolTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new CodeEditSymbolTool(new SymbolService(new ISymbolExtractor[] { new CSharpSymbolExtractor() }));
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private async Task<ToolResult> Edit(string path, string name, string newSource)
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var args = new
        {
            path,
            name,
            new_source = newSource,
        };
        return await _tool.ExecuteAsync(
            Json.Parse(System.Text.Json.JsonSerializer.Serialize(args)), ctx, CancellationToken.None);
    }

    [Test]
    public async Task ReplacesNamedSymbol()
    {
        var file = _ws.WriteFile("Foo.cs", """
            public class Foo
            {
                public int Bar(int x) { return x; }
            }
            """);

        var result = await Edit("Foo.cs", "Bar", "    public int Bar(int x) { return x + 100; }");

        Assert.That(result.IsError, Is.False, result.Content);
        var updated = File.ReadAllText(file);
        Assert.That(updated, Does.Contain("return x + 100;"));
        Assert.That(updated, Does.Contain("public class Foo"));
    }

    [Test]
    public async Task AmbiguousName_Refuses()
    {
        _ws.WriteFile("Two.cs", """
            class A { public void Run() { } }
            class B { public void Run() { } }
            """);

        var result = await Edit("Two.cs", "Run", "public void Run() { }");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("matches 2 symbols"));
        Assert.That(result.Content, Does.Contain("A.Run"));
        Assert.That(result.Content, Does.Contain("B.Run"));
    }

    [Test]
    public async Task ReparseGuard_RejectsBrokenReplacement()
    {
        var file = _ws.WriteFile("Foo.cs", """
            public class Foo
            {
                public int Bar(int x) { return x; }
            }
            """);
        var original = File.ReadAllText(file);

        // Replacement drops the closing brace, which breaks the file.
        var result = await Edit("Foo.cs", "Bar", "    public int Bar(int x) { return x;");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("introduces a parse error"));
        // File must be untouched.
        Assert.That(File.ReadAllText(file), Is.EqualTo(original));
    }

    [Test]
    public async Task MissingSymbol_ReturnsError()
    {
        _ws.WriteFile("Foo.cs", "public class Foo { }");
        var result = await Edit("Foo.cs", "Ghost", "void Ghost() {}");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("No symbol named 'Ghost'"));
    }
}
