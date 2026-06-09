using NUnit.Framework;
using Yamca.Agent.Settings;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsPromptDockPositionTests
{
    [Test]
    public void DefaultsToTop()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null); // first run

        Assert.That(settings.PromptDockPosition, Is.EqualTo(PromptDockPosition.Top));
    }

    [Test]
    public void SurvivesSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetPromptDockPosition(PromptDockPosition.Bottom);

        var json = settings.SerializeUser();

        var reloaded = new SessionSettings();
        reloaded.HydrateUser(json);

        Assert.That(reloaded.PromptDockPosition, Is.EqualTo(PromptDockPosition.Bottom));
    }

    [Test]
    public void MissingFieldHydratesToTop()
    {
        var settings = new SessionSettings();
        // A blob with no promptDockPosition key (e.g. settings written by an older build).
        settings.HydrateUser("{}");

        Assert.That(settings.PromptDockPosition, Is.EqualTo(PromptDockPosition.Top));
    }

    [Test]
    public void SetPromptDockPosition_RaisesChangedForUserTier()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");

        SettingsTier? tier = null;
        settings.Changed += t => tier = t;

        settings.SetPromptDockPosition(PromptDockPosition.Bottom);

        Assert.That(tier, Is.EqualTo(SettingsTier.User));
    }

    [Test]
    public void SetPromptDockPosition_IsIdempotent()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}"); // defaults to Top

        var raised = false;
        settings.Changed += _ => raised = true;

        settings.SetPromptDockPosition(PromptDockPosition.Top); // unchanged

        Assert.That(raised, Is.False);
    }

    [Test]
    public void ResetUserToDefaults_RestoresTop()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetPromptDockPosition(PromptDockPosition.Bottom);

        settings.ResetUserToDefaults(System.Array.Empty<Yamca.Agent.Tools.ITool>());

        Assert.That(settings.PromptDockPosition, Is.EqualTo(PromptDockPosition.Top));
    }
}
