using Yamca.Agent.Mcp;

namespace Yamca.Agent.Tests.Mcp;

[TestFixture]
public class McpConfigFileStoreTests
{
    private string _configDir = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _configDir = Path.Combine(baseDir, "yamca-tests", "mcp-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private McpConfigFileStore NewStore() => new(_configDir);

    private string SettingsPath => Path.Combine(_configDir, "mcp.json");

    [Test]
    public void Save_ThenLoad_RoundTripsBlob()
    {
        var store = NewStore();
        const string blob = "[{\"id\":\"fetch\",\"command\":\"uvx\"}]";

        store.Save(blob);

        Assert.That(store.Load(), Is.EqualTo(blob));
        Assert.That(File.Exists(SettingsPath), Is.True);
    }

    [Test]
    public void Save_CreatesConfigDirectory_WhenAbsent()
    {
        Assert.That(Directory.Exists(_configDir), Is.False);
        var store = NewStore();

        store.Save("[]");

        Assert.That(Directory.Exists(_configDir), Is.True);
    }

    [Test]
    public void Save_Twice_OverwritesInPlace()
    {
        var store = NewStore();

        store.Save("[{\"id\":\"a\"}]");
        store.Save("[{\"id\":\"b\"}]");

        Assert.That(store.Load(), Is.EqualTo("[{\"id\":\"b\"}]"));
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

        store.Save("[]");

        Assert.That(File.Exists(SettingsPath + ".tmp"), Is.False);
    }

    [Test]
    public void FilePath_PointsAtMcpJsonInConfigDir()
    {
        var store = NewStore();

        Assert.That(store.FilePath, Is.EqualTo(SettingsPath));
    }
}
