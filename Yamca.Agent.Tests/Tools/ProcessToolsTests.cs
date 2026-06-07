using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ProcessToolsTests
{
    private static BackgroundProcessManager NewManager() =>
        new(new ShellResolver(new InterpreterResolver()));

    private static ITool[] AllTools(BackgroundProcessManager manager) => new ITool[]
    {
        new StartProcessTool(manager, new InMemorySessionSettings()),
        new GetProcessOutputTool(manager),
        new StopProcessTool(manager),
        new ListProcessesTool(manager),
    };

    [Test]
    public void AllProcessTools_AreDeferred()
    {
        foreach (var tool in AllTools(NewManager()))
            Assert.That(tool.Deferred, Is.True, $"{tool.Name} should be deferred");
    }

    [Test]
    public void Names_MatchExpected()
    {
        Assert.That(AllTools(NewManager()).Select(t => t.Name), Is.EqualTo(new[]
        {
            "start_process", "get_process_output", "stop_process", "list_processes"
        }));
    }

    [Test]
    public void Defaults_MutatingAsk_ReadOnlyAllow()
    {
        var manager = NewManager();
        Assert.That(new StartProcessTool(manager, new InMemorySessionSettings()).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new StopProcessTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new GetProcessOutputTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new ListProcessesTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
    }
}
