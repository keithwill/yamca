using NUnit.Framework;
using Yamca.Agent.Board;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardStoreTests
{
    private string _root = null!;
    private BoardStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "yamca-tests", "bs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // The board is a plain directory; no git repo is required. RepositoryRoot == RootPath here.
        _store = new BoardStore(new WorkspaceImpl(_root, _root));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task EnsureAsync_CreatesPlainBoardDirectory_NotGitTracked()
    {
        var path = await _store.EnsureAsync(CancellationToken.None);

        Assert.That(path, Is.EqualTo(Path.Combine(_root, ".yamca", "board")));
        Assert.That(Directory.Exists(path), Is.True);
        // The board is uncommitted: there is no repository or branch behind it.
        Assert.That(Directory.Exists(Path.Combine(path, ".git")), Is.False);
    }

    [Test]
    public async Task EnsureAsync_SeedsDefaultColumns()
    {
        var path = await _store.EnsureAsync(CancellationToken.None);

        foreach (var (dir, _) in BoardService.DefaultColumns)
        {
            Assert.That(Directory.Exists(Path.Combine(path, dir)), Is.True, dir);
            Assert.That(File.Exists(Path.Combine(path, dir, BoardService.InstructionsFileName)), Is.True, dir);
        }
    }

    [Test]
    public async Task EnsureAsync_FreshInstance_ReusesExistingBoard()
    {
        await _store.EnsureAsync(CancellationToken.None);
        // Drop a card so we can prove a second instance reads the same directory rather than reseeding.
        var card = Path.Combine(_root, ".yamca", "board", "10-idea", "0001-x.md");
        await File.WriteAllTextAsync(card, "# X");

        var other = new BoardStore(new WorkspaceImpl(_root, _root));
        var path = await other.EnsureAsync(CancellationToken.None);

        Assert.That(path, Is.EqualTo(Path.Combine(_root, ".yamca", "board")));
        Assert.That(File.Exists(card), Is.True, "existing board content must be preserved");
    }

    [Test]
    public async Task MutateAsync_SerializesConcurrentWriters()
    {
        await _store.EnsureAsync(CancellationToken.None);

        var inside = 0;
        var maxConcurrent = 0;
        async Task<bool> Body(string _)
        {
            var now = Interlocked.Increment(ref inside);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            return true;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _store.MutateAsync(Body, CancellationToken.None)));

        Assert.That(maxConcurrent, Is.EqualTo(1), "MutateAsync must serialize board writers");
    }

    [Test]
    public async Task MutateAsync_PersistsWritesToDisk()
    {
        await _store.MutateAsync(async board =>
            await File.WriteAllTextAsync(Path.Combine(board, "10-idea", "0001-x.md"), "# X"),
            CancellationToken.None);

        var snapshot = new BoardService().Read(Path.Combine(_root, ".yamca", "board"));
        Assert.That(snapshot.FindCard("0001"), Is.Not.Null);
    }
}
