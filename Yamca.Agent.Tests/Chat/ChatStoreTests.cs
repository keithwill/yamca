using Yamca.Agent.Chat;
using Yamca.Agent.Chat.Persistence;
using Yamca.Agent.Git;
using Yamca.Agent.Workspace;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class ChatStoreTests
{
    private string _repoRoot = null!;
    private IWorkspace _workspace = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _repoRoot = Path.Combine(baseDir, "yamca-tests", "store-" + Guid.NewGuid().ToString("N"));
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

    private ChatStore NewStore() => new(_workspace);

    private static PersistedChat SampleChat(string title = "first question")
    {
        var doc = new PersistedChat
        {
            Id = Guid.NewGuid(),
            Title = title,
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Endpoint = new PersistedEndpoint(Guid.NewGuid(), "local", "http://localhost:8080/v1", "test-model"),
            Worktree = new WorktreeInfo("feature/x", "main", "/tmp/wt", "/tmp/repo"),
            WorkspaceRootPath = "/tmp/repo",
            Compaction = new PersistedCompaction("summary text", 2),
            Messages =
            {
                new ChatMessage(ChatRole.System, "system"),
                new ChatMessage(ChatRole.User, title),
                new ChatMessage(ChatRole.Assistant, "answer"),
            },
            Turns =
            {
                new PersistedTurn
                {
                    UserMessage = title,
                    Items =
                    {
                        new PersistedTurnItem { Kind = "text", Text = "answer", IsComplete = true },
                        new PersistedTurnItem
                        {
                            Kind = "tool", CallId = "c1", ToolName = "read_file",
                            ArgumentsJson = "{}", State = "Succeeded", Result = "ok",
                        },
                    },
                },
            },
        };
        return doc;
    }

    [Test]
    public void Save_ThenLoad_RoundTripsFullDocument()
    {
        Enable();
        var store = NewStore();
        var doc = SampleChat();

        store.Save(doc);
        var loaded = store.Load(doc.Id);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Title, Is.EqualTo("first question"));
        Assert.That(loaded.Endpoint!.Model, Is.EqualTo("test-model"));
        Assert.That(loaded.Worktree!.Branch, Is.EqualTo("feature/x"));
        Assert.That(loaded.Compaction!.BoundaryUiTurnIndex, Is.EqualTo(2));
        Assert.That(loaded.Messages, Has.Count.EqualTo(3));
        Assert.That(loaded.Turns, Has.Count.EqualTo(1));
        Assert.That(loaded.Turns[0].Items, Has.Count.EqualTo(2));
        Assert.That(loaded.Turns[0].Items[1].State, Is.EqualTo("Succeeded"));
    }

    [Test]
    public void Save_ThenLoad_RoundTripsAttachedImages()
    {
        Enable();
        var store = NewStore();
        var doc = SampleChat();
        var image = new ChatImage("image/png", "QUJD");
        // Canonical LLM context: image rides along on the user message.
        doc.Messages[1] = new ChatMessage(ChatRole.User, doc.Title, Images: new[] { image });
        // Display side: image is stored on the turn for thumbnail redisplay.
        doc.Turns[0].Images = new List<ChatImage> { image };

        store.Save(doc);
        var loaded = store.Load(doc.Id);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Messages[1].Images, Has.Count.EqualTo(1));
        Assert.That(loaded.Messages[1].Images![0].MimeType, Is.EqualTo("image/png"));
        Assert.That(loaded.Messages[1].Images![0].Base64Data, Is.EqualTo("QUJD"));
        Assert.That(loaded.Turns[0].Images, Has.Count.EqualTo(1));
        Assert.That(loaded.Turns[0].Images![0].Base64Data, Is.EqualTo("QUJD"));
    }

    [Test]
    public void Save_DoesNotPersistApiKey()
    {
        // The model has no field for a secret; this guards that the snapshot type stays
        // secret-free by confirming the serialized file never contains a key value.
        Enable();
        var store = NewStore();
        var doc = SampleChat();
        store.Save(doc);

        var file = Directory.EnumerateFiles(Path.Combine(_repoRoot, ".yamca", "chat"), "*.json")
            .First(f => !f.EndsWith("index.json", StringComparison.OrdinalIgnoreCase));
        var json = File.ReadAllText(file);

        Assert.That(json, Does.Not.Contain("apiKey"));
        Assert.That(json, Does.Not.Contain("ApiKey"));
    }

    [Test]
    public void List_ReturnsEntriesSortedByUpdatedDescending()
    {
        Enable();
        var store = NewStore();
        var older = SampleChat("older");
        var newer = SampleChat("newer");

        store.Save(older);
        store.Save(newer); // saved second → more recent UpdatedUtc

        var list = store.List();

        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list[0].Title, Is.EqualTo("newer"));
        Assert.That(list[0].Branch, Is.EqualTo("feature/x"));
        Assert.That(list[0].MessageCount, Is.EqualTo(3));
    }

    [Test]
    public void Save_Twice_UpdatesInPlace()
    {
        Enable();
        var store = NewStore();
        var doc = SampleChat("v1");
        store.Save(doc);

        doc.Title = "v2";
        store.Save(doc);

        var list = store.List();
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Title, Is.EqualTo("v2"));
    }

    [Test]
    public void List_SelfHeals_WhenIndexMissing()
    {
        Enable();
        var store = NewStore();
        store.Save(SampleChat("a"));
        store.Save(SampleChat("b"));

        File.Delete(Path.Combine(_repoRoot, ".yamca", "chat", "index.json"));

        var list = store.List();
        Assert.That(list, Has.Count.EqualTo(2));
    }

    [Test]
    public void List_SelfHeals_WhenIndexCorrupt()
    {
        Enable();
        var store = NewStore();
        store.Save(SampleChat("a"));

        File.WriteAllText(Path.Combine(_repoRoot, ".yamca", "chat", "index.json"), "{ not valid json");

        var list = store.List();
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Title, Is.EqualTo("a"));
    }

    [Test]
    public void Delete_RemovesFileAndIndexEntry()
    {
        Enable();
        var store = NewStore();
        var doc = SampleChat();
        store.Save(doc);

        store.Delete(doc.Id);

        Assert.That(store.Load(doc.Id), Is.Null);
        Assert.That(store.List(), Is.Empty);
    }

    [Test]
    public void Load_ReturnsNull_ForUnknownSchemaVersion()
    {
        Enable();
        var store = NewStore();
        var doc = SampleChat();
        store.Save(doc);

        // Tamper with the on-disk schema version.
        var file = Path.Combine(_repoRoot, ".yamca", "chat", doc.Id.ToString("N") + ".json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999"));

        Assert.That(store.Load(doc.Id), Is.Null);
    }

    [Test]
    public void Operations_AreNoOps_OutsideGitRepo()
    {
        // No Enable() call → no managed .gitignore → store disabled.
        var store = NewStore();
        var doc = SampleChat();

        Assert.That(store.IsEnabled, Is.False);

        store.Save(doc);

        Assert.That(store.List(), Is.Empty);
        Assert.That(store.Load(doc.Id), Is.Null);
        Assert.That(Directory.Exists(Path.Combine(_repoRoot, ".yamca", "chat")), Is.False);
    }
}
