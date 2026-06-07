using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ExecuteCommandToolTests
{
    private TempWorkspace _ws = null!;
    private ExecuteCommandTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _tool = new ExecuteCommandTool(new ShellResolver(new InterpreterResolver()), new InMemorySessionSettings());
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private static string EchoHello() =>
        OperatingSystem.IsWindows() ? "echo hello" : "echo hello";

    private static string FailingCommand() =>
        OperatingSystem.IsWindows() ? "exit 7" : "exit 7";

    [Test]
    public async Task CapturesStdoutAndExitCode()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: false);
        var args = Json.Parse($$"""{ "command": {{System.Text.Json.JsonSerializer.Serialize(EchoHello())}} }""");

        var result = await _tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("exit_code: 0"));
        Assert.That(result.Content, Does.Contain("hello"));
    }

    [Test]
    public async Task NonZeroExit_ReturnsError()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: false);
        var args = Json.Parse($$"""{ "command": {{System.Text.Json.JsonSerializer.Serialize(FailingCommand())}} }""");

        var result = await _tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("exit_code: 7"));
    }

    [Test]
    public async Task RunsInWorkspaceRoot()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: false);
        // `pwd` works in PowerShell (alias for Get-Location) and POSIX shells.
        var cmd = "pwd";
        var args = Json.Parse($$"""{ "command": {{System.Text.Json.JsonSerializer.Serialize(cmd)}} }""");

        var result = await _tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        // Working directory should be reported as the workspace root (case may vary on Windows).
        Assert.That(result.Content, Does.Contain(Path.GetFileName(_ws.RootPath)));
    }

    [Test]
    public async Task Timeout_KillsCommand()
    {
        var ctx = new ToolContext(_ws.Workspace, restrictToWorkspace: false);
        // Sleep for longer than the timeout we'll set.
        var cmd = OperatingSystem.IsWindows()
            ? "ping -n 6 127.0.0.1 > nul"
            : "sleep 5";
        var args = Json.Parse($$"""{ "command": {{System.Text.Json.JsonSerializer.Serialize(cmd)}}, "timeout_seconds": 1 }""");

        var result = await _tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("timed out"));
    }
}
