using Yamca.Agent.Settings.Persistence;

namespace Yamca.Agent.Tests.Settings;

[TestFixture]
public class GlobalSettingsStoreTests
{
    private string _configDir = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _configDir = Path.Combine(baseDir, "yamca-tests", "global-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private GlobalSettingsStore NewStore() => new(_configDir);

    private string SettingsPath => Path.Combine(_configDir, "global.json");

    [Test]
    public void Save_ThenLoad_RoundTripsBlob()
    {
        var store = NewStore();
        const string blob = "{\"systemPrompt\":\"hi\",\"endpoints\":[]}";

        store.Save(blob);

        Assert.That(store.Load(), Is.EqualTo(blob));
        Assert.That(File.Exists(SettingsPath), Is.True);
    }

    [Test]
    public void Save_CreatesConfigDirectory_WhenAbsent()
    {
        Assert.That(Directory.Exists(_configDir), Is.False);
        var store = NewStore();

        store.Save("{\"v\":1}");

        Assert.That(Directory.Exists(_configDir), Is.True);
    }

    [Test]
    public void Save_Twice_OverwritesInPlace()
    {
        var store = NewStore();

        store.Save("{\"v\":1}");
        store.Save("{\"v\":2}");

        Assert.That(store.Load(), Is.EqualTo("{\"v\":2}"));
    }

    [Test]
    public void Load_ReturnsNull_WhenFileAbsent()
    {
        var store = NewStore();

        Assert.That(store.Load(), Is.Null);
    }

    [Test]
    public void Save_LeavesNoTempFileBehind()
    {
        var store = NewStore();

        store.Save("{\"v\":1}");

        Assert.That(File.Exists(SettingsPath + ".tmp"), Is.False);
    }

    [Test]
    public void ResolveDefaultDirectory_HonorsEnvOverride()
    {
        const string varName = "YAMCA_CONFIG_DIR";
        var previous = Environment.GetEnvironmentVariable(varName);
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "yamca-override-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(varName, custom);

            Assert.That(GlobalSettingsStore.ResolveDefaultDirectory(), Is.EqualTo(custom));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, previous);
        }
    }

    [Test]
    public void ResolveDefaultDirectory_DefaultsToYamcaFolder()
    {
        const string varName = "YAMCA_CONFIG_DIR";
        var previous = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, null);

            var dir = GlobalSettingsStore.ResolveDefaultDirectory();

            Assert.That(dir, Is.Not.Empty);
            Assert.That(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Is.EqualTo("yamca").Or.EqualTo(".yamca"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, previous);
        }
    }
}
