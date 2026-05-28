using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeFindToolsTests
{
    private const string Sample = """
        class Calc
        {
            // Add helper below
            public int Add(int a, int b)
            {
                return a + b;
            }
            public int Run()
            {
                var note = "remember to Add";
                return Add(1, 2);
            }
        }
        """;

    private TempWorkspace _ws = null!;
    private NodeProfileResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _ws.WriteFile("Calc.cs", Sample);
        _resolver = new NodeProfileResolver(new ILanguageNodeProfile[]
        {
            new GenericNodeProfile(), new CSharpNodeProfile(),
        });
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: true);

    [Test]
    public async Task FindDefinitions_LocatesDeclarationOnly()
    {
        var tool = new CodeFindDefinitionsTool(_resolver);
        var result = await tool.ExecuteAsync(
            Json.Parse("""{ "name": "Add" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:4"));
        Assert.That(result.Content, Does.Contain("method Add"));
        Assert.That(result.Content, Does.Contain("in Calc"));
        // Only the declaration, not the call site.
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:11"));
    }

    [Test]
    public async Task FindCalls_SkipsCommentsAndStrings()
    {
        var tool = new CodeFindCallsTool(_resolver);
        var result = await tool.ExecuteAsync(
            Json.Parse("""{ "name": "Add" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:11"));   // the actual call
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:3")); // the comment
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:10")); // the string literal
    }

    [Test]
    public async Task FindReferences_MatchesIdentifiersNotCommentOrString()
    {
        var tool = new CodeFindReferencesTool(_resolver);
        var result = await tool.ExecuteAsync(
            Json.Parse("""{ "name": "Add" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:4"));   // declaration identifier
        Assert.That(result.Content, Does.Contain("Calc.cs:11"));  // call identifier
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:3"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:10"));
    }

    [Test]
    public async Task FindDefinitions_NoMatch_ReportsEmpty()
    {
        var tool = new CodeFindDefinitionsTool(_resolver);
        var result = await tool.ExecuteAsync(
            Json.Parse("""{ "name": "Nonexistent" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("(no matches for 'Nonexistent')"));
    }
}
