using NUnit.Framework;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsMetricsEnabledTests
{
    [Test]
    public void DefaultsToOn()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null); // first run

        Assert.That(settings.MetricsEnabled, Is.True);
    }

    [Test]
    public void SurvivesSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetMetricsEnabled(false);

        var json = settings.SerializeUser();

        var reloaded = new SessionSettings();
        reloaded.HydrateUser(json);

        Assert.That(reloaded.MetricsEnabled, Is.False);
    }

    [Test]
    public void MissingFieldHydratesToOn()
    {
        var settings = new SessionSettings();
        // A blob with no metricsEnabled key (e.g. settings written by an older build).
        settings.HydrateUser("{}");

        Assert.That(settings.MetricsEnabled, Is.True);
    }

    [Test]
    public void SetMetricsEnabled_RaisesChangedForUserTier()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");

        SettingsTier? tier = null;
        settings.Changed += t => tier = t;

        settings.SetMetricsEnabled(false);

        Assert.That(tier, Is.EqualTo(SettingsTier.User));
    }

    [Test]
    public void ResetUserToDefaults_RestoresOn()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetMetricsEnabled(false);

        settings.ResetUserToDefaults(System.Array.Empty<Yamca.Agent.Tools.ITool>());

        Assert.That(settings.MetricsEnabled, Is.True);
    }

    [Test]
    public void Retention_DefaultsTo50kAnd90Days()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null);

        Assert.That(settings.MetricsRetentionMaxSamples, Is.EqualTo(50_000));
        Assert.That(settings.MetricsRetentionMaxAgeDays, Is.EqualTo(90));
    }

    [Test]
    public void Retention_SurvivesSerializeRoundTrip()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetMetricsRetentionMaxSamples(12_345);
        settings.SetMetricsRetentionMaxAgeDays(7);

        var reloaded = new SessionSettings();
        reloaded.HydrateUser(settings.SerializeUser());

        Assert.That(reloaded.MetricsRetentionMaxSamples, Is.EqualTo(12_345));
        Assert.That(reloaded.MetricsRetentionMaxAgeDays, Is.EqualTo(7));
    }

    [Test]
    public void Retention_SettersClampToRange()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");

        settings.SetMetricsRetentionMaxSamples(1);          // below floor (1,000)
        settings.SetMetricsRetentionMaxAgeDays(-5);         // below floor (0)
        Assert.That(settings.MetricsRetentionMaxSamples, Is.EqualTo(1_000));
        Assert.That(settings.MetricsRetentionMaxAgeDays, Is.EqualTo(0));

        settings.SetMetricsRetentionMaxSamples(int.MaxValue);
        settings.SetMetricsRetentionMaxAgeDays(int.MaxValue);
        Assert.That(settings.MetricsRetentionMaxSamples, Is.EqualTo(5_000_000));
        Assert.That(settings.MetricsRetentionMaxAgeDays, Is.EqualTo(3_650));
    }

    [Test]
    public void ReadMetricsRetention_ParsesBlobAndMapsAgeToTimeSpan()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetMetricsRetentionMaxSamples(2_000);
        settings.SetMetricsRetentionMaxAgeDays(30);
        var json = settings.SerializeUser();

        var retention = SessionSettings.ReadMetricsRetention(json);

        Assert.That(retention.MaxSamples, Is.EqualTo(2_000));
        Assert.That(retention.MaxAge, Is.EqualTo(System.TimeSpan.FromDays(30)));
    }

    [Test]
    public void ReadMetricsRetention_AgeZeroMeansNoCap()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        settings.SetMetricsRetentionMaxAgeDays(0);

        var retention = SessionSettings.ReadMetricsRetention(settings.SerializeUser());

        Assert.That(retention.MaxAge, Is.Null);
    }

    [Test]
    public void ReadMetricsRetention_MissingOrMalformedBlobUsesDefaults()
    {
        foreach (var json in new[] { null, "", "not json", "{}" })
        {
            var retention = SessionSettings.ReadMetricsRetention(json);
            Assert.That(retention.MaxSamples, Is.EqualTo(50_000), $"for input: {json ?? "null"}");
            Assert.That(retention.MaxAge, Is.EqualTo(System.TimeSpan.FromDays(90)), $"for input: {json ?? "null"}");
        }
    }
}
