using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Settings;

[TestFixture]
public class SubagentRegistryTests
{
    private static SubagentDefinition Agent(string name, string description = "") =>
        new(Guid.NewGuid(), name, description, "instructions", new[] { "read_file" });

    [Test]
    public void Merge_ProjectOverridesGlobalByName_CaseInsensitive()
    {
        var global = new SubagentRegistry(new[] { Agent("explorer", "global desc"), Agent("builder") });
        var project = new SubagentRegistry(new[] { Agent("Explorer", "project desc") });

        var merged = SubagentRegistry.Merge(global, project);

        Assert.That(merged, Has.Count.EqualTo(2));
        var explorer = merged.Single(a => string.Equals(a.Name, "Explorer", StringComparison.OrdinalIgnoreCase));
        Assert.That(explorer.Description, Is.EqualTo("project desc"));
        Assert.That(merged.Any(a => a.Name == "builder"));
    }

    [Test]
    public void Merge_AppendsProjectOnlyAgents_PreservingGlobalOrderFirst()
    {
        var global = new SubagentRegistry(new[] { Agent("a"), Agent("b") });
        var project = new SubagentRegistry(new[] { Agent("c") });

        var merged = SubagentRegistry.Merge(global, project);

        Assert.That(merged.Select(a => a.Name), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Resolve_IsCaseInsensitive_AndReturnsNullWhenMissing()
    {
        var global = new SubagentRegistry(new[] { Agent("explorer") });
        var project = SubagentRegistry.Empty;

        Assert.That(SubagentRegistry.Resolve(global, project, "EXPLORER"), Is.Not.Null);
        Assert.That(SubagentRegistry.Resolve(global, project, "missing"), Is.Null);
    }
}
