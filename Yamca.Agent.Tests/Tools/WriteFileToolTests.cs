using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class WriteFileToolTests
{
    private TempWorkspace _ws = null!;
    private WriteFileTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new WriteFileTool();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task CreatesNewFile()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "out.txt", "content": "hi" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "out.txt")), Is.EqualTo("hi"));
    }

    [Test]
    public async Task OverwritesExisting()
    {
        _ws.WriteFile("out.txt", "old");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "out.txt", "content": "new" }"""), ctx, CancellationToken.None);

        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "out.txt")), Is.EqualTo("new"));
    }

    [Test]
    public async Task CreatesParentDirectories()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "nested/dir/out.txt", "content": "x" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_ws.RootPath, "nested", "dir", "out.txt")), Is.True);
    }

    [Test]
    public async Task SandboxedEscape_ReturnsError_AndDoesNotWrite()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "../escape.txt", "content": "x" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(_ws.RootPath)!, "escape.txt")), Is.False);
    }
}
