using System.Diagnostics;
using NUnit.Framework;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class BackgroundProcessManagerTests
{
    private static BackgroundProcessManager NewManager() =>
        new(new ShellResolver(new InterpreterResolver()));

    private static StartRequest Request(string name, string command) =>
        new(name, command, Path.GetTempPath(), StopCommand: null, Array.Empty<int>(), ShellPreference.Auto);

    // A command that keeps running for a while, regardless of the resolved host shell.
    private static string LongRunningCommand() =>
        OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1" : "sleep 60";

    [Test]
    public async Task Start_CapturesOutput_AndMarksExit()
    {
        await using var manager = NewManager();

        var outcome = manager.Start(Request("echoer", "echo background-hello"));
        Assert.That(outcome.AlreadyRunning, Is.False);

        var process = outcome.Process;
        Assert.That(manager.Snapshot().Select(p => p.Name), Does.Contain("echoer"));

        // Wait for the echo to flush and the process to exit (output events can lag the exit).
        await WaitUntil(() =>
            process.Status != ProcessStatus.Running &&
            process.RenderTail().Contains("background-hello", StringComparison.Ordinal));

        Assert.That(process.RenderTail(), Does.Contain("background-hello"));
        Assert.That(process.Status, Is.EqualTo(ProcessStatus.Exited));
        Assert.That(process.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task Start_SameNameWhileRunning_ReturnsExistingHandle()
    {
        await using var manager = NewManager();

        var first = manager.Start(Request("server", LongRunningCommand()));
        Assert.That(first.AlreadyRunning, Is.False);
        Assert.That(first.Process.Status, Is.EqualTo(ProcessStatus.Running));

        var second = manager.Start(Request("server", LongRunningCommand()));

        Assert.That(second.AlreadyRunning, Is.True);
        Assert.That(second.Process, Is.SameAs(first.Process), "dedupe-by-name must not spawn a duplicate");
        Assert.That(manager.Snapshot().Count(p => p.Name == "server"), Is.EqualTo(1));

        await manager.Stop("server");
    }

    [Test]
    public async Task Stop_TerminatesRunningProcess()
    {
        await using var manager = NewManager();

        var process = manager.Start(Request("sleeper", LongRunningCommand())).Process;
        Assert.That(process.Status, Is.EqualTo(ProcessStatus.Running));

        var stopped = await manager.Stop("sleeper");
        Assert.That(stopped, Is.True);

        await WaitUntil(() => process.Status != ProcessStatus.Running);
        Assert.That(process.Status, Is.Not.EqualTo(ProcessStatus.Running));
    }

    [Test]
    public async Task Stop_UnknownName_ReturnsFalse()
    {
        await using var manager = NewManager();
        Assert.That(await manager.Stop("nope"), Is.False);
    }

    [Test]
    public async Task GetOutput_IncrementalCursor_ReturnsOnlyNewLines()
    {
        await using var manager = NewManager();

        var process = manager.Start(Request("two-lines", "echo one && echo two")).Process;
        await WaitUntil(() => process.Status != ProcessStatus.Running && process.RenderTail().Contains("two"));

        var full = manager.GetOutput("two-lines", sinceSeq: null)!;
        Assert.That(full.Lines.Select(l => l.Text), Has.Some.Contains("one"));

        // Reading from the last cursor yields nothing new.
        var tail = manager.GetOutput("two-lines", sinceSeq: full.NextCursor)!;
        Assert.That(tail.Lines, Is.Empty);
        Assert.That(tail.NextCursor, Is.EqualTo(full.NextCursor));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Condition not met within timeout.");
    }
}
