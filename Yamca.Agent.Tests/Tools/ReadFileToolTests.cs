using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ReadFileToolTests
{
    private TempWorkspace _ws = null!;
    private ReadFileTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new ReadFileTool();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task ReadsExistingFile()
    {
        _ws.WriteFile("hello.txt", "hello world");
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "hello.txt" }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Is.EqualTo("     1\thello world\n"));
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
    public async Task MissingPathArg_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: true);

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ }"""), ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Missing required argument 'path'"));
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

    [Test]
    public async Task Unsandboxed_AllowsOutsidePath()
    {
        // When the sandbox is off, the tool should reach a path outside the workspace.
        var outsideFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(outsideFile, "outside content");
            var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: false);

            var result = await _tool.ExecuteAsync(
                Json.Parse($$"""{ "path": {{System.Text.Json.JsonSerializer.Serialize(outsideFile)}} }"""),
                ctx, CancellationToken.None);

            Assert.That(result.IsError, Is.False, result.Content);
            Assert.That(result.Content, Is.EqualTo("     1\toutside content\n"));
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }
}
