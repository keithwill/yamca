using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeExtractSymbolToolTests
{
    private TempWorkspace _ws = null!;
    private CodeExtractSymbolTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new CodeExtractSymbolTool(new SymbolService(new ISymbolExtractor[] { new CSharpSymbolExtractor() }));
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private async Task<ToolResult> Run(string path, string name)
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        return await _tool.ExecuteAsync(
            Json.Parse($$"""{ "path": "{{path}}", "name": "{{name}}" }"""), ctx, CancellationToken.None);
    }

    [Test]
    public async Task SingleMatch_ReturnsSymbolSource()
    {
        _ws.WriteFile("Foo.cs", """
            public class Foo
            {
                public int Bar(int x)
                {
                    return x + 1;
                }
            }
            """);

        var result = await Run("Foo.cs", "Bar");

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Foo.Bar  [Foo.cs:3-6]"));
        Assert.That(result.Content, Does.Contain("public int Bar(int x)"));
        Assert.That(result.Content, Does.Contain("return x + 1;"));
        // Should not include the closing brace of the class.
        Assert.That(result.Content, Does.Not.Contain("class Foo"));
    }

    [Test]
    public async Task QualifiedName_DisambiguatesNestedMembers()
    {
        _ws.WriteFile("Two.cs", """
            class A { public void Run() { } }
            class B { public void Run() { } }
            """);

        var ambiguous = await Run("Two.cs", "Run");
        Assert.That(ambiguous.IsError, Is.False);
        Assert.That(ambiguous.Content, Does.Contain("Multiple symbols match 'Run'"));
        Assert.That(ambiguous.Content, Does.Contain("A.Run"));
        Assert.That(ambiguous.Content, Does.Contain("B.Run"));

        var qualified = await Run("Two.cs", "B.Run");
        Assert.That(qualified.IsError, Is.False, qualified.Content);
        Assert.That(qualified.Content, Does.Contain("B.Run"));
        Assert.That(qualified.Content, Does.Not.Contain("Multiple symbols"));
    }

    [Test]
    public async Task NoMatch_ListsTopLevelSymbols()
    {
        _ws.WriteFile("Foo.cs", "class Alpha { } class Beta { }");

        var result = await Run("Foo.cs", "Nope");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("No symbol named 'Nope'"));
        Assert.That(result.Content, Does.Contain("Alpha"));
        Assert.That(result.Content, Does.Contain("Beta"));
    }

    [Test]
    public async Task MissingFile_ReturnsError()
    {
        var result = await Run("ghost.cs", "X");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("File not found"));
    }
}
