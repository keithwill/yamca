using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class DeleteFileToolTests
{
    private TempWorkspace _ws = null!;
    private DeleteFileTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new DeleteFileTool();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task DeletesExistingFile()
    {
        _ws.WriteFile("doomed.txt", "x");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "doomed.txt" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(File.Exists(Path.Combine(_ws.RootPath, "doomed.txt")), Is.False);
    }

    [Test]
    public async Task MissingFile_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "missing.txt" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("File not found"));
    }

    [Test]
    public async Task RefusesDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_ws.RootPath, "subdir"));
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "subdir" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("is a directory"));
        Assert.That(Directory.Exists(Path.Combine(_ws.RootPath, "subdir")), Is.True);
    }

    [Test]
    public async Task SandboxedEscape_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "../escape.txt" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("outside the workspace root"));
    }
}
