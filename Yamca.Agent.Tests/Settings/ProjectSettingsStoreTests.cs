using Yamca.Agent.Settings.Persistence;
using Yamca.Agent.Workspace;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Settings;

[TestFixture]
public class ProjectSettingsStoreTests
{
    private string _repoRoot = null!;
    private IWorkspace _workspace = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _repoRoot = Path.Combine(baseDir, "yamca-tests", "project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        _workspace = new WorkspaceImpl(_repoRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    // Marks the workspace as a git repo as far as the store is concerned (its IsEnabled
    // guard keys off the managed .yamca/.gitignore that WorkspaceScaffold writes).
    private void Enable() => WorkspaceScaffold.EnsureGitignore(_repoRoot);

    private ProjectSettingsStore NewStore() => new(_workspace);

    private string SettingsPath => Path.Combine(_repoRoot, ".yamca", "project.json");

    [Test]
    public void Save_ThenLoad_RoundTripsBlob()
    {
        Enable();
        var store = NewStore();
        const string blob = "{\"tools\":{\"read_file\":{\"permission\":\"Allow\"}}}";

        store.Save(blob);

        Assert.That(store.Load(), Is.EqualTo(blob));
        Assert.That(File.Exists(SettingsPath), Is.True);
    }

    [Test]
    public void Save_Twice_OverwritesInPlace()
    {
        Enable();
        var store = NewStore();

        store.Save("{\"v\":1}");
        store.Save("{\"v\":2}");

        Assert.That(store.Load(), Is.EqualTo("{\"v\":2}"));
    }

    [Test]
    public void Load_ReturnsNull_WhenFileAbsent()
    {
        Enable();
        var store = NewStore();

        Assert.That(store.Load(), Is.Null);
    }

    [Test]
    public void Save_LeavesNoTempFileBehind()
    {
        Enable();
        var store = NewStore();

        store.Save("{\"v\":1}");

        Assert.That(File.Exists(SettingsPath + ".tmp"), Is.False);
    }

    [Test]
    public void Operations_AreNoOps_OutsideGitRepo()
    {
        // No Enable() call → no managed .gitignore → store disabled.
        var store = NewStore();

        Assert.That(store.IsEnabled, Is.False);

        store.Save("{\"v\":1}");

        Assert.That(store.Load(), Is.Null);
        Assert.That(File.Exists(SettingsPath), Is.False);
    }
}
