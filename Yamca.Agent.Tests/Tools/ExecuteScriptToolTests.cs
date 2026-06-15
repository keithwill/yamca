using System.Text.Json;
using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ExecuteScriptToolTests
{
    private static ExecuteScriptTool NewTool(InMemorySessionSettings settings)
    {
        var interpreters = new InterpreterResolver();
        var runner = new ScriptRunner(interpreters, new ShellResolver(interpreters));
        return new ExecuteScriptTool(runner, new ScriptRegistryLookup(settings));
    }

    [Test]
    public void Tool_DefaultsAsk_ExposedToLlm_SupportsWorkspaceRestriction()
    {
        ITool tool = NewTool(new InMemorySessionSettings());
        Assert.That(tool.Name, Is.EqualTo("execute_script"));
        Assert.That(tool.DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(tool.ExposedToLlm, Is.True);
        Assert.That(tool.SupportsWorkspaceRestriction, Is.True);
        // Plain Ask tool — permission is user-configurable.
        Assert.That(tool.ConfigurablePermission, Is.True);
    }

    [Test]
    public async Task RegisteredPath_IsRefused_PointingAtExecuteAllowed()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                new[] { new RegisteredScript("build.ps1", "build") },
                Array.Empty<RegisteredScriptDirectory>(),
                Array.Empty<RegisteredInlineScript>()),
        };
        var tool = NewTool(settings);

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "script_path": "build.ps1" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("execute_allowed"));
    }

    [Test]
    public async Task FileUnderRegisteredDirectory_IsRefused()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = new ScriptRegistry(
                Array.Empty<RegisteredScript>(),
                new[] { new RegisteredScriptDirectory(".scripts", "all") },
                Array.Empty<RegisteredInlineScript>()),
        };
        var tool = NewTool(settings);

        using var ws = new TempWorkspace();
        var context = new ToolContext(ws.Workspace, restrictToWorkspace: false);
        using var args = JsonDocument.Parse("""{ "script_path": ".scripts/deploy.sh" }""");

        var result = await tool.ExecuteAsync(args.RootElement, context, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("execute_allowed"));
    }
}
