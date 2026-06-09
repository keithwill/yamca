using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class CodeListSymbolsToolTests
{
    private TempWorkspace _ws = null!;
    private SymbolService _symbols = null!;
    private CodeListSymbolsTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _symbols = new SymbolService(new ISymbolExtractor[] { new CSharpSymbolExtractor() });
        _tool = new CodeListSymbolsTool(_symbols);
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task FileMode_CSharp_ProducesIndentedTree()
    {
        _ws.WriteFile("Foo.cs", """
            namespace Acme;

            public class Foo
            {
                public void Bar(int x) { }
            }
            """);
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Foo.cs" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Foo.cs\n"));
        Assert.That(result.Content, Does.Contain("  namespace Acme  [L1]"));
        Assert.That(result.Content, Does.Contain("    class Foo  [L3]"));
        Assert.That(result.Content, Does.Contain("Bar(int x)"));
        Assert.That(result.Content, Does.Contain("[L5]"));
    }

    [Test]
    public async Task FileMode_UnsupportedExtension_ReturnsError()
    {
        _ws.WriteFile("notes.txt", "hello");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "notes.txt" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Unsupported file type"));
    }

    [Test]
    public async Task FileMode_MissingPath_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "missing.cs" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Path not found"));
    }

    [Test]
    public async Task FileMode_SandboxEscape_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "../escape.cs" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("outside the workspace root"));
    }

    [Test]
    public async Task DirectoryPath_ReturnsError()
    {
        _ws.WriteFile("nested/A.cs", "class A { void X() {} }");

        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("single file"));
    }

    [Test]
    public async Task CacheHit_PopulatesCache()
    {
        var path = _ws.WriteFile("Hit.cs", "class Hit { }");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var first = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Hit.cs" }"""), ctx, CancellationToken.None);

        // Delete the underlying file. The path-existence check runs before the loader, so
        // the second call still surfaces a not-found error; we validate the cache via Count.
        File.Delete(path);

        var second = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Hit.cs" }"""), ctx, CancellationToken.None);

        Assert.That(first.IsError, Is.False);
        Assert.That(_symbols.CacheCount, Is.EqualTo(1));
        Assert.That(second.IsError, Is.True);
        Assert.That(second.Content, Does.Contain("Path not found"));
    }

    [Test]
    public async Task CacheInvalidates_WhenMtimeChanges()
    {
        var path = _ws.WriteFile("Bump.cs", "class Old { }");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var first = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Bump.cs" }"""), ctx, CancellationToken.None);

        Assert.That(first.Content, Does.Contain("class Old"));

        // Rewrite with new content + new mtime.
        await Task.Delay(50);
        File.WriteAllText(path, "class New { void Hi() { } }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var second = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Bump.cs" }"""), ctx, CancellationToken.None);

        Assert.That(second.IsError, Is.False);
        Assert.That(second.Content, Does.Contain("class New"));
        Assert.That(second.Content, Does.Not.Contain("class Old"));
    }
}
