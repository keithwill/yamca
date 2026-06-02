using NUnit.Framework;
using Yamca.Agent.Settings;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsSubagentTests
{
    [Test]
    public void FirstRun_SeedsExplorerSubagent()
    {
        var settings = new SessionSettings();
        settings.HydrateGlobal(null); // first run

        Assert.That(settings.GlobalSubagents.Agents.Select(a => a.Name), Does.Contain("explorer"));
        var explorer = settings.GlobalSubagents.Agents.Single(a => a.Name == "explorer");
        Assert.That(explorer.AllowedTools, Does.Contain("grep"));
        Assert.That(explorer.AllowedTools, Has.None.StartsWith("code_"));
    }

    [Test]
    public void GlobalSubagents_SurviveSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateGlobal("{}"); // not first run → starts empty
        var agent = new SubagentDefinition(
            Guid.NewGuid(), "reviewer", "reviews code", "Be thorough.",
            new[] { "read_file", "grep" },
            RestrictToWorkspace: false, RequireApproval: true,
            EndpointId: Guid.NewGuid(), MaxIterations: 25);
        settings.SetSubagents(SettingsTier.Global, new SubagentRegistry(new[] { agent }));

        var json = settings.SerializeGlobal();
        var reloaded = new SessionSettings();
        reloaded.HydrateGlobal(json);

        var got = reloaded.GlobalSubagents.Agents.Single();
        Assert.That(got.Name, Is.EqualTo("reviewer"));
        Assert.That(got.AllowedTools, Is.EqualTo(new[] { "read_file", "grep" }));
        Assert.That(got.RestrictToWorkspace, Is.False);
        Assert.That(got.RequireApproval, Is.True);
        Assert.That(got.EndpointId, Is.EqualTo(agent.EndpointId));
        Assert.That(got.MaxIterations, Is.EqualTo(25));
    }

    [Test]
    public void ProjectSubagents_SurviveSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        var agent = new SubagentDefinition(
            Guid.NewGuid(), "local", "project agent", "Local instructions.",
            new[] { "list_directory" });
        settings.SetSubagents(SettingsTier.Project, new SubagentRegistry(new[] { agent }));

        var json = settings.SerializeProject();
        var reloaded = new SessionSettings();
        reloaded.HydrateProject(json);

        Assert.That(reloaded.ProjectSubagents.Agents.Single().Name, Is.EqualTo("local"));
    }
}
