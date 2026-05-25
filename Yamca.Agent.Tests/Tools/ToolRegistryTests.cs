using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;

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
        new ExecuteCommandTool(),
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
    public void GetChatTools_ReturnsOneEntryPerTool_WithSchemaJson()
    {
        var registry = NewRegistry();

        var chatTools = registry.GetChatTools();

        Assert.That(chatTools.Count, Is.EqualTo(5));
        for (var i = 0; i < chatTools.Count; i++)
        {
            var payload = chatTools[i].ParametersJsonSchema;
            Assert.That(payload, Does.Contain("\"type\""));
            Assert.That(payload, Does.Contain("\"properties\""));
        }
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
