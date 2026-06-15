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
        settings.HydrateUser(null); // first run

        Assert.That(settings.UserSubagents.Agents.Select(a => a.Name), Does.Contain("explorer"));
        var explorer = settings.UserSubagents.Agents.Single(a => a.Name == "explorer");
        Assert.That(explorer.AllowedTools, Does.Contain("grep"));
        Assert.That(explorer.AllowedTools, Has.None.StartsWith("code_"));
    }

    [Test]
    public void FirstRun_SeedsCodeSubagent()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null); // first run

        Assert.That(settings.UserSubagents.Agents.Select(a => a.Name), Does.Contain("code"));
        var code = settings.UserSubagents.Agents.Single(a => a.Name == "code");

        // The implementer edits files directly and can run allowed commands/scripts to build/test.
        Assert.That(code.AllowedTools, Does.Contain("write_file"));
        Assert.That(code.AllowedTools, Does.Contain("edit_file"));
        Assert.That(code.AllowedTools, Does.Contain("execute_allowed"));
        // It is gated to the curated registry, not free-form command execution, by default.
        Assert.That(code.AllowedTools, Has.None.EqualTo("execute_command"));
        // Inherits the global iteration cap rather than pinning its own.
        Assert.That(code.MaxIterations, Is.Null);
    }

    [Test]
    public void UserSubagents_SurviveSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}"); // not first run → starts empty
        var agent = new SubagentDefinition(
            Guid.NewGuid(), "reviewer", "reviews code", "Be thorough.",
            new[] { "read_file", "grep" },
            RestrictToWorkspace: false, RequireApproval: true,
            EndpointId: Guid.NewGuid(), MaxIterations: 25);
        settings.SetSubagents(SettingsTier.User, new SubagentRegistry(new[] { agent }));

        var json = settings.SerializeUser();
        var reloaded = new SessionSettings();
        reloaded.HydrateUser(json);

        var got = reloaded.UserSubagents.Agents.Single();
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
