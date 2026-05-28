using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeSurroundingContextToolTests
{
    private TempWorkspace _ws = null!;
    private CodeSurroundingContextTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new CodeSurroundingContextTool(new SymbolService(new ISymbolExtractor[] { new CSharpSymbolExtractor() }));
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private async Task<ToolResult> Run(string path, int line)
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        return await _tool.ExecuteAsync(
            Json.Parse($$"""{ "path": "{{path}}", "line": {{line}} }"""), ctx, CancellationToken.None);
    }

    [Test]
    public async Task LineInsideNestedMethod_ReportsDeepestSymbolAndChain()
    {
        _ws.WriteFile("Foo.cs", """
            namespace Acme
            {
                public class Foo
                {
                    public void Bar()
                    {
                        var hit = 1;
                    }
                }
            }
            """);
        // Line 7 is `var hit = 1;` inside Bar.
        var result = await Run("Foo.cs", 7);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Foo.cs:7"));
        Assert.That(result.Content, Does.Contain("namespace Acme > class Foo"));
        Assert.That(result.Content, Does.Contain("Bar"));
        Assert.That(result.Content, Does.Contain("Acme.Foo.Bar"));
    }

    [Test]
    public async Task LineOutsideAnySymbol_ReportsNearestAbove()
    {
        _ws.WriteFile("Foo.cs", """
            class A { }

            class B { }
            """);
        // Line 2 is blank, between the two top-level classes.
        var result = await Run("Foo.cs", 2);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("nearest above").Or.Contain("not inside"));
    }

    [Test]
    public async Task MissingLine_ReturnsError()
    {
        _ws.WriteFile("Foo.cs", "class A { }");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Foo.cs" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("'line' is required"));
    }
}
