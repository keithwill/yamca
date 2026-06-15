using System.Text.Json;
using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ExecuteAllowedToolTests
{
    // Records the start request instead of spawning a real process.
    private sealed class FakeProcessManager : IBackgroundProcessManager
    {
        public StartRequest? Started { get; private set; }

        public StartOutcome Start(StartRequest request)
        {
            Started = request;
            return new StartOutcome(new BackgroundProcess(request, ShellKind.Pwsh), AlreadyRunning: false);
        }

        public event Action? Changed { add { } remove { } }
        public IReadOnlyList<BackgroundProcess> Snapshot() => Array.Empty<BackgroundProcess>();
        public BackgroundProcess? Get(string name) => null;
        public OutputSnapshot? GetOutput(string name, long? sinceSeq) => null;
        public Task<bool> Stop(string name) => Task.FromResult(false);
        public Task<BackgroundProcess?> Restart(string name) => Task.FromResult<BackgroundProcess?>(null);
        public Task StopAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ScriptRunner NewRunner()
    {
        var interpreters = new InterpreterResolver();
        return new ScriptRunner(interpreters, new ShellResolver(interpreters));
    }

    private static ExecuteAllowedTool NewTool(InMemorySessionSettings settings, IBackgroundProcessManager manager) =>
        new(NewRunner(), new ScriptRegistryLookup(settings), settings, manager);

    [Test]
    public void Tool_IsAlwaysAllow_NotConfigurable()
    {
        ITool tool = NewTool(new InMemorySessionSettings(), new FakeProcessManager());
        Assert.That(tool.Name, Is.EqualTo("execute_allowed"));
        Assert.That(tool.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(tool.ConfigurablePermission, Is.False);
        Assert.That(tool.SupportsWorkspaceRestriction, Is.False);
        Assert.That(tool.ExposedToLlm, Is.True);
        Assert.That(tool.ExposedInSettings, Is.True);
    }

    [Test]
    public async Task BackgroundCommand_ByName_IsLaunchedAsProcess()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                Array.Empty<RegisteredScript>(),
                Array.Empty<RegisteredScriptDirectory>(),
                new[] { new RegisteredInlineScript("npm run watch", "Watcher", Name: "watch", Background: true) }),
        };
        var manager = new FakeProcessManager();
        var tool = NewTool(settings, manager);

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "command_or_script": "watch" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("Started 'watch'"));
        Assert.That(manager.Started, Is.Not.Null);
        Assert.That(manager.Started!.Name, Is.EqualTo("watch"));
        Assert.That(manager.Started!.Command, Is.EqualTo("npm run watch"));
        Assert.That(manager.Started!.WorkingDirectory, Is.EqualTo(ws.RootPath));
    }

    [Test]
    public async Task CommandName_WinsOverScriptPath()
    {
        // A registered (background) command named "build" and a registered script file also at the
        // path "build": resolving "build" must pick the command (proved by the background launch).
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                new[] { new RegisteredScript("build", "a file") },
                Array.Empty<RegisteredScriptDirectory>(),
                new[] { new RegisteredInlineScript("npm run build", "the command", Name: "build", Background: true) }),
        };
        var manager = new FakeProcessManager();
        var tool = NewTool(settings, manager);

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "command_or_script": "build" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(manager.Started, Is.Not.Null, "the registered command should have won over the script path");
        Assert.That(manager.Started!.Command, Is.EqualTo("npm run build"));
    }

    [Test]
    public async Task UnregisteredTarget_ReturnsError_PointingAtExecuteScript()
    {
        var settings = new InMemorySessionSettings();
        var tool = NewTool(settings, new FakeProcessManager());

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "command_or_script": "tools/whatever.ps1" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("execute_script"));
    }

    [Test]
    public async Task RegisteredCommand_ByName_RunsToCompletion()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                Array.Empty<RegisteredScript>(),
                Array.Empty<RegisteredScriptDirectory>(),
                new[] { new RegisteredInlineScript("echo hello", "Greet", Name: "greet") }),
        };
        var tool = NewTool(settings, new FakeProcessManager());

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "command_or_script": "greet" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("exit_code: 0"));
        Assert.That(result.Content, Does.Contain("hello"));
    }
}
