using NUnit.Framework;
using Yamca.Agent.Mcp;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class McpConfigStoreTests
{
    private string _configDir = null!;

    [SetUp]
    public void SetUp() =>
        _configDir = Path.Combine(Path.GetTempPath(), "yamca-tests", "mcpstore-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private async Task<(McpConfigStore store, McpRegistry registry)> NewStoreAsync()
    {
        var registry = new McpRegistry();
        var store = new McpConfigStore(new McpConfigFileStore(_configDir), registry);
        await store.HydrateAsync();
        return (store, registry);
    }

    [Test]
    public async Task Hydrate_FreshInstall_SeedsDisabledDefaults()
    {
        var (store, registry) = await NewStoreAsync();
        await using var _ = registry;

        Assert.That(store.Configs.Select(c => c.Id),
            Is.EquivalentTo(new[] { "chrome-devtools", "playwright" }));
        Assert.That(store.Configs.All(c => !c.Enabled), Is.True);
        // Seed was materialized to disk so the null-means-seed check fires only once.
        Assert.That(File.Exists(store.FilePath), Is.True);
    }

    [Test]
    public async Task RestoreMissingDefaults_AddsOnlyDeletedDefault_PreservingEdits()
    {
        var (store, registry) = await NewStoreAsync();
        await using var _ = registry;

        // User removes one default and edits the other's args.
        await store.RemoveAsync("playwright");
        await store.ReplaceAsync("chrome-devtools", overrideId: "chrome-devtools",
            json: """{ "config": { "command": "npx", "args": ["-y", "chrome-devtools-mcp@1.2.3"] } }""");

        var added = await store.RestoreMissingDefaultsAsync();

        Assert.That(added, Is.EqualTo(1)); // only the deleted playwright comes back
        Assert.That(store.Configs.Select(c => c.Id),
            Is.EquivalentTo(new[] { "chrome-devtools", "playwright" }));
        // The user's edit to the kept default is untouched.
        var chrome = store.Configs.Single(c => c.Id == "chrome-devtools");
        Assert.That(chrome.Stdio!.Args, Does.Contain("chrome-devtools-mcp@1.2.3"));
    }

    [Test]
    public async Task RestoreMissingDefaults_NothingMissing_ReturnsZero()
    {
        var (store, registry) = await NewStoreAsync();
        await using var _ = registry;

        Assert.That(await store.RestoreMissingDefaultsAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Hydrate_EmptyListOnDisk_DoesNotReseed()
    {
        // A present-but-empty file is a deliberate "user removed everything" state.
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "mcp.json"), "[]");

        var (store, registry) = await NewStoreAsync();
        await using var _ = registry;

        Assert.That(store.Configs, Is.Empty);
    }
}
