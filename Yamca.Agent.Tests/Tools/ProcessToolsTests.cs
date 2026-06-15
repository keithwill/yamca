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

    private static ScriptRegistryLookup NewRegistry() =>
        new(new InMemorySessionSettings());

    // The facade resolves permission services lazily from the scope; these tests only inspect
    // metadata (never ExecuteAsync), so an empty provider suffices.
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static StartProcessTool NewStartProcess() =>
        new(NewManager(), new InMemorySessionSettings(), NewRegistry(), new NullServiceProvider());

    private static ITool[] AllTools(BackgroundProcessManager manager) => new ITool[]
    {
        new StartProcessTool(manager, new InMemorySessionSettings(), NewRegistry(), new NullServiceProvider()),
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
    public void StartProcess_IsFacade_AllowPassthrough_HiddenFromSettings()
    {
        var tool = NewStartProcess();
        // Passes the AgentLoop gate so it can resolve the real identity internally, and isn't a
        // settings row itself — the identities are execute_allowed (always Allow) / start_process_command.
        Assert.That(tool.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(tool.ExposedInSettings, Is.False);
    }

    [Test]
    public void StartProcessCommandIdentity_DefaultsAsk_HiddenFromLlm()
    {
        var identity = new StartProcessCommandTool();
        Assert.That(identity.Name, Is.EqualTo("start_process_command"));
        Assert.That(identity.DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(identity.ExposedToLlm, Is.False);
        Assert.That(identity.Deferred, Is.True);
    }

    [Test]
    public void Defaults_MutatingAsk_ReadOnlyAllow()
    {
        var manager = NewManager();
        Assert.That(new StopProcessTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new GetProcessOutputTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new ListProcessesTool(manager).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
    }
}
