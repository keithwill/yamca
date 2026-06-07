using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Permissions;

[TestFixture]
public class PermissionResolverTests
{
    private static (PermissionResolver Resolver, InMemorySessionSettings Settings) NewResolver()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new ReadFileTool(),       // default Allow
            new WriteFileTool(),      // default Ask
            new ExecuteCommandTool(new ShellResolver(new InterpreterResolver()), new InMemorySessionSettings()), // default Ask, not sandboxable
        });
        var settings = new InMemorySessionSettings();
        return (new PermissionResolver(registry, settings), settings);
    }

    private static ToolSettingsMap Map(string toolName, PermissionLevel? level = null, bool? restrict = null)
        => new(new[]
        {
            new KeyValuePair<string, ToolPermissionSettings>(
                toolName,
                new ToolPermissionSettings { Permission = level, RestrictToWorkspace = restrict })
        });

    [Test]
    public void NothingConfigured_FallsThroughToToolDefault()
    {
        var (resolver, _) = NewResolver();

        Assert.That(resolver.Resolve("read_file"), Is.EqualTo(PermissionLevel.Allow));
        Assert.That(resolver.Resolve("write_file"), Is.EqualTo(PermissionLevel.Allow));
        Assert.That(resolver.Resolve("execute_command"), Is.EqualTo(PermissionLevel.Ask));
    }

    [Test]
    public void UserOverrides_ToolDefault()
    {
        var (resolver, settings) = NewResolver();
        settings.User = Map("read_file", level: PermissionLevel.Deny);

        Assert.That(resolver.Resolve("read_file"), Is.EqualTo(PermissionLevel.Deny));
    }

    [Test]
    public void ProjectOverrides_User()
    {
        var (resolver, settings) = NewResolver();
        settings.User = Map("write_file", level: PermissionLevel.Allow);
        settings.Project = Map("write_file", level: PermissionLevel.Deny);

        Assert.That(resolver.Resolve("write_file"), Is.EqualTo(PermissionLevel.Deny));
    }

    [Test]
    public void ProjectUnset_FallsThroughToUser()
    {
        var (resolver, settings) = NewResolver();
        settings.User = Map("write_file", level: PermissionLevel.Allow);
        settings.Project = Map("write_file"); // no Permission set

        Assert.That(resolver.Resolve("write_file"), Is.EqualTo(PermissionLevel.Allow));
    }

    [Test]
    public void UnknownTool_DefaultsToAsk()
    {
        var (resolver, _) = NewResolver();

        Assert.That(resolver.Resolve("ghost_tool"), Is.EqualTo(PermissionLevel.Ask));
    }

    [Test]
    public void RestrictToWorkspace_DefaultsTrueForSandboxable_FalseForExec()
    {
        var (resolver, _) = NewResolver();

        Assert.That(resolver.RestrictToWorkspace("read_file"), Is.True);
        Assert.That(resolver.RestrictToWorkspace("execute_command"), Is.False);
    }

    [Test]
    public void RestrictToWorkspace_ProjectOverridesUserOverridesDefault()
    {
        var (resolver, settings) = NewResolver();

        settings.User = Map("write_file", restrict: false);
        Assert.That(resolver.RestrictToWorkspace("write_file"), Is.False);

        settings.Project = Map("write_file", restrict: true);
        Assert.That(resolver.RestrictToWorkspace("write_file"), Is.True);
    }
}
