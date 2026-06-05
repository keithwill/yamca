using NUnit.Framework;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ScriptRunnerInlineTests
{
    private TempWorkspace _ws = null!;
    private ScriptRunner _runner = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        var interpreters = new InterpreterResolver();
        _runner = new ScriptRunner(interpreters, new ShellResolver(interpreters));
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private ToolContext Context() => new(_ws.Workspace, restrictToWorkspace: false);

    // pwsh and POSIX shells both treat `echo hello` the same way.
    private static string EchoFour() => OperatingSystem.IsWindows()
        ? "Write-Output a; Write-Output b; Write-Output c; Write-Output d"
        : "printf 'a\\nb\\nc\\nd\\n'";

    [Test]
    public async Task RunInline_CapturesStdoutAndExitZero()
    {
        var result = await _runner.RunInlineAsync("echo hello", timeoutSeconds: 30, maxOutputLines: null, Context(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("exit_code: 0"));
        Assert.That(result.Content, Does.Contain("hello"));
    }

    [Test]
    public async Task RunInline_NonZeroExit_ReturnsError()
    {
        var result = await _runner.RunInlineAsync("exit 7", timeoutSeconds: 30, maxOutputLines: null, Context(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("exit_code: 7"));
    }

    [Test]
    public async Task RunInline_SuppressOutputOnSuccess_WithholdsStdout()
    {
        var result = await _runner.RunInlineAsync("echo secret", timeoutSeconds: 30, maxOutputLines: null, Context(), CancellationToken.None, suppressOutputOnSuccess: true);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Not.Contain("secret"));
    }

    [Test]
    public async Task RunInline_SuppressOutputOnSuccess_StillReturnsOutputOnFailure()
    {
        // echo runs first (exit 0) then exit 3 — the non-zero exit must yield full output.
        var cmd = OperatingSystem.IsWindows()
            ? "Write-Output diagnostic; exit 3"
            : "echo diagnostic; exit 3";
        var result = await _runner.RunInlineAsync(cmd, timeoutSeconds: 30, maxOutputLines: null, Context(), CancellationToken.None, suppressOutputOnSuccess: true);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("exit_code: 3"));
        Assert.That(result.Content, Does.Contain("diagnostic"));
    }

    [Test]
    public async Task RunInline_MaxOutputLines_KeepsLastLines()
    {
        var result = await _runner.RunInlineAsync(EchoFour(), timeoutSeconds: 30, maxOutputLines: 2, Context(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        // Last two lines kept, earliest dropped with a marker.
        Assert.That(result.Content, Does.Contain("c"));
        Assert.That(result.Content, Does.Contain("d"));
        Assert.That(result.Content, Does.Contain("earlier output truncated"));
        // The first line should have been dropped.
        Assert.That(result.Content, Does.Not.Contain("\na\n"));
    }
}
