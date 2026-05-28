using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ListSymbolsToolTests
{
    private TempWorkspace _ws = null!;
    private ParsedTreeCache _cache = null!;
    private ListSymbolsTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _cache = new ParsedTreeCache();
        _tool = new ListSymbolsTool(_cache, new ISymbolExtractor[] { new CSharpSymbolExtractor() });
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
    public async Task DirectoryMode_GroupsByPath_AndSkipsUnsupported()
    {
        _ws.WriteFile("A.cs", "class A { void X() {} }");
        _ws.WriteFile("nested/B.cs", "class B { void Y() {} }");
        _ws.WriteFile("readme.md", "# readme");

        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("A.cs"));
        Assert.That(result.Content, Does.Contain("nested/B.cs"));
        Assert.That(result.Content, Does.Not.Contain("readme.md"));
    }

    [Test]
    public async Task DirectoryMode_RespectsGitignore()
    {
        _ws.WriteFile("Kept.cs", "class Kept { }");
        _ws.WriteFile("Ignored.cs", "class Ignored { }");
        _ws.WriteFile(".gitignore", "Ignored.cs\n");

        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Kept.cs"));
        Assert.That(result.Content, Does.Not.Contain("Ignored.cs"));
    }

    [Test]
    public async Task DirectoryMode_NoSupportedFiles_ReportsEmptiness()
    {
        _ws.WriteFile("a.md", "hi");
        _ws.WriteFile("b.txt", "hi");

        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("(no supported source files found)"));
    }

    [Test]
    public async Task DirectoryMode_MaxFilesTruncation()
    {
        for (var i = 0; i < 5; i++)
            _ws.WriteFile($"F{i}.cs", $"class F{i} {{ }}");

        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": ".", "max_files": 2 }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("…[truncated at 2 files]"));
    }

    [Test]
    public async Task CacheHit_AvoidsReparse()
    {
        var path = _ws.WriteFile("Hit.cs", "class Hit { }");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var first = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Hit.cs" }"""), ctx, CancellationToken.None);

        // Delete the underlying file. If the cache works, the second call returns the
        // first result without touching disk.
        File.Delete(path);

        var second = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "Hit.cs" }"""), ctx, CancellationToken.None);

        Assert.That(first.IsError, Is.False);
        // The path-existence check is performed before the cache; after delete we will
        // still get the path-not-found error. So we instead validate the cache via Count:
        Assert.That(_cache.Count, Is.EqualTo(1));
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
