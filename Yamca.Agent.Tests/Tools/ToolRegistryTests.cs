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
    public void GetChatTools_ExcludesDeferredTools_WhenNotLoaded()
    {
        var registry = NewRegistry();

        var chatTools = registry.GetChatTools(new LoadedToolSet());

        // delete_file and execute_command are deferred — should not appear in the initial list.
        Assert.That(chatTools.Select(t => t.Name), Is.EquivalentTo(new[]
        {
            "read_file", "write_file", "list_directory"
        }));
        foreach (var t in chatTools)
        {
            Assert.That(t.ParametersJsonSchema, Does.Contain("\"type\""));
            Assert.That(t.ParametersJsonSchema, Does.Contain("\"properties\""));
        }
    }

    [Test]
    public void GetChatTools_IncludesDeferredTools_OnceLoaded()
    {
        var registry = NewRegistry();
        var loaded = new LoadedToolSet();
        loaded.MarkLoaded("delete_file");

        var chatTools = registry.GetChatTools(loaded);

        Assert.That(chatTools.Select(t => t.Name), Is.EquivalentTo(new[]
        {
            "read_file", "write_file", "list_directory", "delete_file"
        }));
    }

    [Test]
    public void GetDeferredTools_ReturnsOnlyDeferredOnes()
    {
        var registry = NewRegistry();

        Assert.That(registry.GetDeferredTools().Select(t => t.Name), Is.EquivalentTo(new[]
        {
            "delete_file", "execute_command"
        }));
    }

    [Test]
    public void Defaults_ReadAndListAreAllow_OthersAsk()
    {
        var registry = NewRegistry();

        Assert.That(registry.Get("read_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("list_directory")!.DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(registry.Get("write_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(registry.Get("delete_file")!.DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
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
