using NUnit.Framework;
using Yamca.Agent.Tools.ShellExecution;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsShellPreferenceTests
{
    [Test]
    public void DefaultsToAuto()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null); // first run

        Assert.That(settings.ShellPreference, Is.EqualTo(ShellPreference.Auto));
    }

    [Test]
    public void SurvivesSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetShellPreference(ShellPreference.GitBash);

        var json = settings.SerializeUser();

        var reloaded = new SessionSettings();
        reloaded.HydrateUser(json);

        Assert.That(reloaded.ShellPreference, Is.EqualTo(ShellPreference.GitBash));
    }

    [Test]
    public void MissingFieldHydratesToAuto()
    {
        var settings = new SessionSettings();
        // A blob with no shellPreference key (e.g. settings written by an older build).
        settings.HydrateUser("{}");

        Assert.That(settings.ShellPreference, Is.EqualTo(ShellPreference.Auto));
    }

    [Test]
    public void SetShellPreference_RaisesChangedForUserTier()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");

        SettingsTier? tier = null;
        settings.Changed += t => tier = t;

        settings.SetShellPreference(ShellPreference.Pwsh);

        Assert.That(tier, Is.EqualTo(SettingsTier.User));
    }

    [Test]
    public void ResetUserToDefaults_RestoresAuto()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetShellPreference(ShellPreference.Cmd);

        settings.ResetUserToDefaults(System.Array.Empty<Yamca.Agent.Tools.ITool>());

        Assert.That(settings.ShellPreference, Is.EqualTo(ShellPreference.Auto));
    }
}
