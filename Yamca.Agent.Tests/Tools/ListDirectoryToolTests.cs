using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ListDirectoryToolTests
{
    private TempWorkspace _ws = null!;
    private ListDirectoryTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new ListDirectoryTool();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task ListsRoot_WithDirectoriesMarked()
    {
        _ws.WriteFile("a.txt", "a");
        _ws.WriteFile("b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_ws.RootPath, "sub"));
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("sub/"));
        Assert.That(result.Content, Does.Contain("a.txt"));
        Assert.That(result.Content, Does.Contain("b.txt"));
    }

    [Test]
    public async Task EmptyDirectory_Reported()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("(empty)"));
    }

    [Test]
    public async Task MissingDirectory_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "nope" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Directory not found"));
    }

    [Test]
    public async Task SandboxedEscape_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": ".." }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("outside the workspace root"));
    }
}
