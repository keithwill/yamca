using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeSearchToolTests
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
    private CodeSearchTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _ws.WriteFile("Calc.cs", Sample);
        var resolver = new NodeProfileResolver(new ILanguageNodeProfile[]
        {
            new GenericNodeProfile(), new CSharpNodeProfile(),
        });
        _tool = new CodeSearchTool(resolver);
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private async Task<ToolResult> Search(string body)
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        return await _tool.ExecuteAsync(Json.Parse(body), ctx, CancellationToken.None);
    }

    [Test]
    public async Task Identifiers_MatchCodeButNotCommentsOrStrings()
    {
        var result = await Search("""{ "pattern": "Add", "in": "identifiers" }""");

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:4"));
        Assert.That(result.Content, Does.Contain("Calc.cs:11"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:3"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:10"));
    }

    [Test]
    public async Task Comments_MatchOnlyComments()
    {
        var result = await Search("""{ "pattern": "Add", "in": "comments" }""");

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:3"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:4"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:11"));
    }

    [Test]
    public async Task Strings_MatchOnlyStringLiterals()
    {
        var result = await Search("""{ "pattern": "Add", "in": "strings" }""");

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Calc.cs:10"));
        Assert.That(result.Content, Does.Not.Contain("Calc.cs:4"));
    }

    [Test]
    public async Task InvalidIn_ReturnsError()
    {
        var result = await Search("""{ "pattern": "Add", "in": "bananas" }""");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("must be one of"));
    }

    [Test]
    public async Task GenericFallback_WorksForRoutedLanguageWithoutDedicatedProfile()
    {
        // Ruby is routed by LanguageRouter but has no extractor or dedicated node profile,
        // so it exercises the generic fallback (comment detection here).
        _ws.WriteFile("greet.rb", """
            # greet says Add
            def greet
              puts "Add"
            end
            """);

        var result = await Search("""{ "pattern": "Add", "in": "comments", "glob": "**/*.rb" }""");

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("greet.rb:1"));
        Assert.That(result.Content, Does.Not.Contain("greet.rb:3")); // the string, not a comment
    }
}
