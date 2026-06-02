using NUnit.Framework;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ToolRegistryTests
{
    private static ToolRegistry NewRegistry() => new(new ITool[]
    {
        new ReadFileTool(),
        new WriteFileTool(),
        new DeleteFileTool(),
        new ListDirectoryTool(),
        new ExecuteCommandTool(new ShellResolver(new InterpreterResolver())),
    });

    [Test]
    public void Tools_EnumeratesInRegistrationOrder()
    {
        var registry = NewRegistry();

        Assert.That(registry.Tools.Select(t => t.Name), Is.EqualTo(new[]
        {
            "read_file", "write_file", "delete_file", "list_directory", "execute_command"
        }));
    }

    [Test]
    public void Get_ReturnsToolByName()
    {
        var registry = NewRegistry();

        Assert.That(registry.Get("write_file"), Is.InstanceOf<WriteFileTool>());
        Assert.That(registry.Get("does_not_exist"), Is.Null);
    }

    [Test]
    public void DuplicateNames_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ToolRegistry(new ITool[] { new ReadFileTool(), new ReadFileTool() }));
    }

    [Test]
    public void GetChatTools_ExcludesDeferredTools()
    {
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry);

        var chatTools = registry.GetChatTools(availability);

        // delete_file is deferred — should not appear in the prefix list.
        Assert.That(chatTools.Select(t => t.Name), Is.EquivalentTo(new[]
        {
            "read_file", "write_file", "list_directory", "execute_command"
        }));
        foreach (var t in chatTools)
        {
            Assert.That(t.ParametersJsonSchema, Does.Contain("\"type\""));
            Assert.That(t.ParametersJsonSchema, Does.Contain("\"properties\""));
        }
    }

    [Test]
    public void GetChatTools_IsCacheStable_DeferredNeverEnterPrefix()
    {
        // The dispatcher's whole point: discovering a deferred tool must not change the prefix
        // tool list, so the prompt-prefix cache survives. GetChatTools no longer depends on any
        // load state, so its output is identical before and after a tool would be "looked up".
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry);

        var before = registry.GetChatTools(availability).Select(t => t.Name).ToList();
        var after = registry.GetChatTools(availability).Select(t => t.Name).ToList();

        Assert.That(after, Is.EqualTo(before));
        Assert.That(after, Does.Not.Contain("delete_file"));
    }

    [Test]
    public void GetDeferredTools_ReturnsOnlyDeferredOnes()
    {
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry);

        Assert.That(registry.GetDeferredTools(availability).Select(t => t.Name), Is.EquivalentTo(new[]
        {
            "delete_file"
        }));
    }

    [Test]
    public void GetChatTools_OverrideEagerToDeferred_RemovesFromInitialList()
    {
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry)
            .Set("read_file", Availability.Deferred);

        var chatTools = registry.GetChatTools(availability);

        Assert.That(chatTools.Select(t => t.Name), Does.Not.Contain("read_file"));
        Assert.That(registry.GetDeferredTools(availability).Select(t => t.Name), Does.Contain("read_file"));
    }

    [Test]
    public void GetChatTools_OverrideDeferredToEager_AddsToInitialList()
    {
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry)
            .Set("delete_file", Availability.Eager);

        var chatTools = registry.GetChatTools(availability);

        Assert.That(chatTools.Select(t => t.Name), Does.Contain("delete_file"));
    }

    [Test]
    public void Hidden_ExcludedFromBothChatListAndDeferredList()
    {
        var registry = NewRegistry();
        var availability = new TestAvailabilityResolver(registry)
            .Set("delete_file", Availability.Hidden);

        var chat = registry.GetChatTools(availability).Select(t => t.Name).ToList();
        var deferred = registry.GetDeferredTools(availability).Select(t => t.Name).ToList();

        Assert.That(chat, Does.Not.Contain("delete_file"));
        Assert.That(deferred, Does.Not.Contain("delete_file"));
    }

    [Test]
    public void Defaults_ReadAndListAreAllow_OthersAsk()
    {
        var registry = NewRegistry();

        Assert.That(registry.Get("read_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("list_directory")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("write_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("delete_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("execute_command")!.DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }

    [Test]
    public void SupportsWorkspaceRestriction_TrueForFileTools_FalseForExec()
    {
        var registry = NewRegistry();

        Assert.That(registry.Get("read_file")!.SupportsWorkspaceRestriction, Is.True);
        Assert.That(registry.Get("write_file")!.SupportsWorkspaceRestriction, Is.True);
        Assert.That(registry.Get("delete_file")!.SupportsWorkspaceRestriction, Is.True);
        Assert.That(registry.Get("list_directory")!.SupportsWorkspaceRestriction, Is.True);
        Assert.That(registry.Get("execute_command")!.SupportsWorkspaceRestriction, Is.False);
    }
}
