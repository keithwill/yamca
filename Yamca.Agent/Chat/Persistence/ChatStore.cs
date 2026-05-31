using System.Text.Json;
using System.Text.Json.Serialization;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat.Persistence;

/// <summary>Reads and writes chat-session history under
/// <c>&lt;RepositoryRoot&gt;/.yamca/chat</c>. Anchors on the supplied workspace's
/// <see cref="IWorkspace.RepositoryRoot"/> (the main repo) — callers should pass the
/// DI-singleton root workspace, never a per-session worktree workspace, so all chats
/// (including worktree-bound ones) live in one place that outlives any worktree.
///
/// All operations are no-ops outside a git repository (see <see cref="IsEnabled"/>) so
/// we never scatter local state into a non-repo workspace. A lightweight
/// <c>index.json</c> backs listing and self-heals by rescanning when missing or
/// unparseable. Reads/writes are serialized through a lock — fine for the single-user,
/// per-circuit usage here.</summary>
public sealed class ChatStore
{
    public const int SchemaVersion = 1;

    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Indented so the files are pleasant to inspect by hand — they're local-only dev state.
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWorkspace _workspace;
    private readonly object _gate = new();

    public ChatStore(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    private string ChatDir => Path.Combine(_workspace.RepositoryRoot, ".yamca", "chat");
    private string IndexPath => Path.Combine(ChatDir, IndexFileName);
    private string SessionPath(Guid id) => Path.Combine(ChatDir, id.ToString("N") + ".json");

    /// <summary>True only when yamca's managed <c>.yamca/.gitignore</c> is present — i.e.
    /// we are inside a git repository where <see cref="WorkspaceScaffold.EnsureGitignore"/>
    /// has set up ignore rules. Outside a repo every method is a no-op.</summary>
    public bool IsEnabled =>
        File.Exists(Path.Combine(_workspace.RepositoryRoot, ".yamca", ".gitignore"));

    /// <summary>Persist <paramref name="doc"/>, stamping its version and update time, and
    /// upsert its index entry. Assigns a fresh <see cref="PersistedChat.Id"/> if unset.</summary>
    public void Save(PersistedChat doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!IsEnabled) return;

        if (doc.Id == Guid.Empty) doc.Id = Guid.NewGuid();
        doc.SchemaVersion = SchemaVersion;
        doc.UpdatedUtc = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Directory.CreateDirectory(ChatDir);
            WriteAtomic(SessionPath(doc.Id), JsonSerializer.Serialize(doc, JsonOptions));

            var index = ReadIndexLocked();
            index.RemoveAll(e => e.Id == doc.Id);
            index.Add(ToListItem(doc));
            WriteIndexLocked(index);
        }
    }

    /// <summary>All saved sessions, most-recently-updated first.</summary>
    public IReadOnlyList<ChatListItem> List()
    {
        if (!IsEnabled) return Array.Empty<ChatListItem>();
        lock (_gate)
        {
            return ReadIndexLocked()
                .OrderByDescending(e => e.UpdatedUtc)
                .ToList();
        }
    }

    /// <summary>Load a full session, or null when missing, unreadable, or a schema
    /// version this build doesn't understand.</summary>
    public PersistedChat? Load(Guid id)
    {
        if (!IsEnabled) return null;
        lock (_gate)
        {
            var path = SessionPath(id);
            if (!File.Exists(path)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<PersistedChat>(File.ReadAllText(path), JsonOptions);
                return doc is not null && doc.SchemaVersion == SchemaVersion ? doc : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Delete(Guid id)
    {
        if (!IsEnabled) return;
        lock (_gate)
        {
            try
            {
                var path = SessionPath(id);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best-effort */ }

            var index = ReadIndexLocked();
            if (index.RemoveAll(e => e.Id == id) > 0)
                WriteIndexLocked(index);
        }
    }

    private static ChatListItem ToListItem(PersistedChat doc) => new(
        doc.Id, doc.Title, doc.Worktree?.Branch, doc.CreatedUtc, doc.UpdatedUtc, doc.Messages.Count);

    /// <summary>Read the index, self-healing by rescanning session files when it is
    /// absent or corrupt. Returns an empty list (without touching disk) when no chat
    /// directory exists yet.</summary>
    private List<ChatListItem> ReadIndexLocked()
    {
        if (!Directory.Exists(ChatDir)) return new List<ChatListItem>();

        if (File.Exists(IndexPath))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<ChatListItem>>(File.ReadAllText(IndexPath), JsonOptions);
                if (items is not null) return items;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // fall through to rebuild
            }
        }

        var rebuilt = RebuildIndexLocked();
        WriteIndexLocked(rebuilt);
        return rebuilt;
    }

    private List<ChatListItem> RebuildIndexLocked()
    {
        var list = new List<ChatListItem>();
        if (!Directory.Exists(ChatDir)) return list;

        foreach (var file in Directory.EnumerateFiles(ChatDir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var doc = JsonSerializer.Deserialize<PersistedChat>(File.ReadAllText(file), JsonOptions);
                if (doc is not null && doc.SchemaVersion == SchemaVersion)
                    list.Add(ToListItem(doc));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // skip unreadable file
            }
        }
        return list;
    }

    private void WriteIndexLocked(List<ChatListItem> index)
    {
        Directory.CreateDirectory(ChatDir);
        WriteAtomic(IndexPath, JsonSerializer.Serialize(index, JsonOptions));
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
