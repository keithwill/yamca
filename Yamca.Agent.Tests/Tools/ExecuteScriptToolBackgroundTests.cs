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
public class ExecuteScriptToolBackgroundTests
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

    private sealed class AllowResolver : IPermissionResolver
    {
        public PermissionLevel Resolve(string toolName) => PermissionLevel.Allow;
        public bool RestrictToWorkspace(string toolName) => false;
    }

    private sealed class StubProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPermissionResolver) ? new AllowResolver() : null;
    }

    [Test]
    public async Task BackgroundInlineCommand_IsLaunchedAsProcess_NotRunToCompletion()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                Array.Empty<RegisteredScript>(),
                Array.Empty<RegisteredScriptDirectory>(),
                new[] { new RegisteredInlineScript("npm run watch", "Watcher", Name: "watch", Background: true) }),
        };
        var manager = new FakeProcessManager();
        var runner = new ScriptRunner(new InterpreterResolver(), new ShellResolver(new InterpreterResolver()));
        var tool = new ExecuteScriptTool(runner, new ScriptRegistryLookup(settings), settings, manager, new StubProvider());

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "script_path": "watch" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("Started 'watch'"));
        Assert.That(manager.Started, Is.Not.Null);
        Assert.That(manager.Started!.Name, Is.EqualTo("watch"));
        Assert.That(manager.Started!.Command, Is.EqualTo("npm run watch"));
        Assert.That(manager.Started!.WorkingDirectory, Is.EqualTo(ws.RootPath));
    }
}
