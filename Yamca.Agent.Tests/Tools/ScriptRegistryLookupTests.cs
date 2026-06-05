using NUnit.Framework;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ScriptRegistryLookupTests
{
    private static ScriptRegistry InlineOnly(params RegisteredInlineScript[] inline) =>
        new(Array.Empty<RegisteredScript>(), Array.Empty<RegisteredScriptDirectory>(), inline);

    [Test]
    public void TryGetInline_MatchesExactCommand()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = InlineOnly(new RegisteredInlineScript("npm install", "Install deps")),
        };
        var lookup = new ScriptRegistryLookup(settings);

        Assert.That(lookup.TryGetInline("npm install", out var entry), Is.True);
        Assert.That(entry.Command, Is.EqualTo("npm install"));
    }

    [Test]
    public void TryGetInline_TrimsSurroundingWhitespace()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = InlineOnly(new RegisteredInlineScript("dotnet build", null)),
        };
        var lookup = new ScriptRegistryLookup(settings);

        Assert.That(lookup.TryGetInline("  dotnet build  ", out _), Is.True);
    }

    [Test]
    public void TryGetInline_RejectsNearMissAndUnregistered()
    {
        var settings = new InMemorySessionSettings
        {
            UserScripts = InlineOnly(new RegisteredInlineScript("dotnet build", null)),
        };
        var lookup = new ScriptRegistryLookup(settings);

        // Extra argument is NOT the same exact command — must not match.
        Assert.That(lookup.TryGetInline("dotnet build --configuration Release", out _), Is.False);
        // Case differs — inline matching is case-sensitive.
        Assert.That(lookup.TryGetInline("Dotnet Build", out _), Is.False);
        // Entirely unregistered.
        Assert.That(lookup.TryGetInline("rm -rf /", out _), Is.False);
    }

    [Test]
    public void TryGetInline_PrefersProjectTier()
    {
        var settings = new InMemorySessionSettings
        {
            ProjectScripts = InlineOnly(new RegisteredInlineScript("build", "project")),
            UserScripts = InlineOnly(new RegisteredInlineScript("build", "user")),
        };
        var lookup = new ScriptRegistryLookup(settings);

        Assert.That(lookup.TryGetInline("build", out var entry), Is.True);
        Assert.That(entry.Description, Is.EqualTo("project"));
    }

    [Test]
    public void AllInline_TagsTiers()
    {
        var settings = new InMemorySessionSettings
        {
            ProjectScripts = InlineOnly(new RegisteredInlineScript("p", null)),
            UserScripts = InlineOnly(new RegisteredInlineScript("u", null)),
        };
        var lookup = new ScriptRegistryLookup(settings);

        var all = lookup.AllInline().ToList();
        Assert.That(all, Has.Count.EqualTo(2));
        Assert.That(all[0].Tier, Is.EqualTo(SettingsTierTag.Project));
        Assert.That(all[1].Tier, Is.EqualTo(SettingsTierTag.User));
    }
}
