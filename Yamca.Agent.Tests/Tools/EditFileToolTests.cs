using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class EditFileToolTests
{
    private TempWorkspace _ws = null!;
    private EditFileTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new EditFileTool();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: true);

    [Test]
    public async Task ReplacesUniqueMatch()
    {
        _ws.WriteFile("a.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "world", "new_string": "there" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "a.txt")), Is.EqualTo("hello there"));
    }

    [Test]
    public async Task OldStringNotFound_ReturnsError()
    {
        _ws.WriteFile("a.txt", "hello world");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "missing", "new_string": "x" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "a.txt")), Is.EqualTo("hello world"));
    }

    [Test]
    public async Task MultipleMatchesWithoutReplaceAll_ReturnsError()
    {
        _ws.WriteFile("a.txt", "foo foo foo");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "foo", "new_string": "bar" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("3"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "a.txt")), Is.EqualTo("foo foo foo"));
    }

    [Test]
    public async Task MultipleMatchesWithReplaceAll_ReplacesAll()
    {
        _ws.WriteFile("a.txt", "foo foo foo");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "foo", "new_string": "bar", "replace_all": true }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "a.txt")), Is.EqualTo("bar bar bar"));
    }

    [Test]
    public async Task EmptyNewString_DeletesMatchedText()
    {
        _ws.WriteFile("a.txt", "keep [drop] keep");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": " [drop]", "new_string": "" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_ws.RootPath, "a.txt")), Is.EqualTo("keep keep"));
    }

    [Test]
    public async Task FileDoesNotExist_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "nope.txt", "old_string": "a", "new_string": "b" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task EmptyOldString_ReturnsError()
    {
        _ws.WriteFile("a.txt", "hello");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "", "new_string": "x" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task IdenticalOldAndNew_ReturnsError()
    {
        _ws.WriteFile("a.txt", "hello");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "hello", "new_string": "hello" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task SandboxedEscape_ReturnsError()
    {
        var outsidePath = Path.Combine(Path.GetDirectoryName(_ws.RootPath)!, "escape.txt");
        await File.WriteAllTextAsync(outsidePath, "secret");
        try
        {
            var result = await _tool.ExecuteAsync(
                Json.Parse("""{ "path": "../escape.txt", "old_string": "secret", "new_string": "pwned" }"""), Ctx(), CancellationToken.None);

            Assert.That(result.IsError, Is.True);
            Assert.That(await File.ReadAllTextAsync(outsidePath), Is.EqualTo("secret"));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Test]
    public async Task MissingRequiredArgument_ReturnsError()
    {
        _ws.WriteFile("a.txt", "hello");

        var result = await _tool.ExecuteAsync(
            Json.Parse("""{ "path": "a.txt", "old_string": "hello" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
    }
}
