using NUnit.Framework;
using Yamca.Agent.Settings;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsOrchestratorTests
{
    [Test]
    public void HydrateProject_MissingBlob_YieldsDefaults()
    {
        var settings = new SessionSettings();
        settings.HydrateProject(null);

        Assert.That(settings.Orchestrator, Is.EqualTo(OrchestratorSettings.Default));
    }

    [Test]
    public void HydrateProject_BlobWithoutOrchestrator_YieldsDefaults()
    {
        var settings = new SessionSettings();
        settings.HydrateProject("{}");

        Assert.That(settings.Orchestrator, Is.EqualTo(OrchestratorSettings.Default));
    }

    [Test]
    public void Orchestrator_SurvivesSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        var endpointId = Guid.NewGuid();
        settings.SetOrchestrator(OrchestratorSettings.Default with
        {
            EnabledColumns = new[] { "20-analyze", "30-implement" },
            EndpointId = endpointId,
            MaxConcurrentRuns = 3,
            MaxConcurrentRunsPerColumn = 1,
            MaxTurnsPerRun = 6,
            MaxToolIterationsPerTurn = 40,
            StallTimeoutSeconds = 120,
            TurnTimeoutSeconds = 900,
            RetryMaxAttempts = 5,
            RetryBaseDelaySeconds = 10,
            RetryMaxDelaySeconds = 300,
            PollIntervalSeconds = 15,
            AllowedTools = new[] { "read_file", "board_move_card" },
            RestrictToWorkspace = false,
        });

        var json = settings.SerializeProject();
        var reloaded = new SessionSettings();
        reloaded.HydrateProject(json);

        var got = reloaded.Orchestrator;
        Assert.That(got.EnabledColumns, Is.EqualTo(new[] { "20-analyze", "30-implement" }));
        Assert.That(got.EndpointId, Is.EqualTo(endpointId));
        Assert.That(got.MaxConcurrentRuns, Is.EqualTo(3));
        Assert.That(got.MaxConcurrentRunsPerColumn, Is.EqualTo(1));
        Assert.That(got.MaxTurnsPerRun, Is.EqualTo(6));
        Assert.That(got.MaxToolIterationsPerTurn, Is.EqualTo(40));
        Assert.That(got.StallTimeoutSeconds, Is.EqualTo(120));
        Assert.That(got.TurnTimeoutSeconds, Is.EqualTo(900));
        Assert.That(got.RetryMaxAttempts, Is.EqualTo(5));
        Assert.That(got.RetryBaseDelaySeconds, Is.EqualTo(10));
        Assert.That(got.RetryMaxDelaySeconds, Is.EqualTo(300));
        Assert.That(got.PollIntervalSeconds, Is.EqualTo(15));
        Assert.That(got.AllowedTools, Is.EqualTo(new[] { "read_file", "board_move_card" }));
        Assert.That(got.RestrictToWorkspace, Is.False);
    }

    [Test]
    public void DefaultOrchestrator_IsOmittedFromProjectJson()
    {
        var settings = new SessionSettings();

        var json = settings.SerializeProject();

        Assert.That(json, Does.Not.Contain("orchestrator"));
    }

    [Test]
    public void SetOrchestrator_ClampsNumericFields()
    {
        var settings = new SessionSettings();
        settings.SetOrchestrator(OrchestratorSettings.Default with
        {
            MaxConcurrentRuns = 999,
            MaxTurnsPerRun = 0,
            StallTimeoutSeconds = 1,
            RetryMaxAttempts = -4,
            PollIntervalSeconds = 100_000,
        });

        var got = settings.Orchestrator;
        Assert.That(got.MaxConcurrentRuns, Is.EqualTo(16));
        Assert.That(got.MaxTurnsPerRun, Is.EqualTo(1));
        Assert.That(got.StallTimeoutSeconds, Is.EqualTo(10));
        Assert.That(got.RetryMaxAttempts, Is.EqualTo(0));
        Assert.That(got.PollIntervalSeconds, Is.EqualTo(300));
    }

    [Test]
    public void SetOrchestrator_NormalizesColumnAndToolLists()
    {
        var settings = new SessionSettings();
        settings.SetOrchestrator(OrchestratorSettings.Default with
        {
            EnabledColumns = new[] { " 20-analyze ", "20-ANALYZE", "", "30-implement" },
            AllowedTools = new[] { "read_file", "read_file", "  ", "git " },
        });

        var got = settings.Orchestrator;
        Assert.That(got.EnabledColumns, Is.EqualTo(new[] { "20-analyze", "30-implement" }));
        Assert.That(got.AllowedTools, Is.EqualTo(new[] { "read_file", "git" }));
    }

    [Test]
    public void SetOrchestrator_RaisesProjectChanged()
    {
        var settings = new SessionSettings();
        SettingsTier? raised = null;
        settings.Changed += tier => raised = tier;

        settings.SetOrchestrator(OrchestratorSettings.Default with { MaxConcurrentRuns = 4 });

        Assert.That(raised, Is.EqualTo(SettingsTier.Project));
    }

    [Test]
    public void HydrateProject_ClampsOutOfRangeBlobValues()
    {
        var settings = new SessionSettings();
        settings.HydrateProject("""{"orchestrator":{"maxConcurrentRuns":500,"pollIntervalSeconds":0}}""");

        Assert.That(settings.Orchestrator.MaxConcurrentRuns, Is.EqualTo(16));
        Assert.That(settings.Orchestrator.PollIntervalSeconds, Is.EqualTo(2));
    }
}
