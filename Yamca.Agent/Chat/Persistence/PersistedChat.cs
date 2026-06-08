using Yamca.Agent.Git;

namespace Yamca.Agent.Chat.Persistence;

/// <summary>On-disk representation of one chat session, stored as a single JSON file
/// under <c>&lt;RepositoryRoot&gt;/.yamca/chat/&lt;id&gt;.json</c>.
///
/// A conversation has two divergent representations and we persist both, because
/// neither reconstructs the other faithfully:
/// <list type="bullet">
/// <item><see cref="Messages"/> is the canonical LLM context — what gets sent to the
/// model on resume. After compaction, earlier messages are removed and a summary is
/// folded into the system message.</item>
/// <item><see cref="Turns"/> is the canonical display — what the user sees. After
/// compaction all turns remain visible, and it carries reasoning text and per-tool
/// success/fail/denied state that the message log does not.</item>
/// </list>
/// Secrets are never written: <see cref="PersistedEndpoint"/> omits the API key, which
/// is re-resolved from settings by id at resume time.</summary>
public sealed class PersistedChat
{
    public int SchemaVersion { get; set; } = ChatStore.SchemaVersion;

    /// <summary>Stable identifier; also the file name. Distinct from the volatile 1–4
    /// in-memory slot id.</summary>
    public Guid Id { get; set; }

    /// <summary>Denormalized label for cheap listing without parsing the whole file. Holds the
    /// effective title — the user's <see cref="CustomTitle"/> if set, otherwise the derived
    /// first-message summary.</summary>
    public string Title { get; set; } = "";

    /// <summary>User-supplied name that overrides the derived title everywhere it's shown.
    /// Null/absent (the default, and in older files) means "use the derived title".</summary>
    public string? CustomTitle { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Non-secret snapshot of the locked endpoint, or null if the chat never
    /// started a turn.</summary>
    public PersistedEndpoint? Endpoint { get; set; }

    /// <summary>Set when the chat was bound to a git worktree. Drives the read-only /
    /// resume decision on reload.</summary>
    public WorktreeInfo? Worktree { get; set; }

    /// <summary>Sandbox root (<c>IWorkspace.RootPath</c>) at save time; informational.</summary>
    public string? WorkspaceRootPath { get; set; }

    public PersistedCompaction? Compaction { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();

    public List<PersistedTurn> Turns { get; set; } = new();
}

/// <summary>Non-secret endpoint snapshot. The API key is intentionally excluded and
/// re-resolved from current settings by <see cref="Id"/> when the chat resumes.</summary>
public sealed record PersistedEndpoint(Guid Id, string? Name, string BaseUrl, string Model);

/// <summary>Restores the "earlier turns summarized" divider on reload.</summary>
public sealed record PersistedCompaction(string Summary, int BoundaryUiTurnIndex);

public sealed class PersistedTurn
{
    public string UserMessage { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>Images the user attached to this turn, for redisplaying thumbnails on
    /// reload. Null/absent for turns without image attachments (and in older files).</summary>
    public List<ChatImage>? Images { get; set; }

    public List<PersistedTurnItem> Items { get; set; } = new();
}

/// <summary>A single display item within a turn. A flat shape (rather than a polymorphic
/// hierarchy) keeps System.Text.Json round-tripping simple. <see cref="Kind"/> selects
/// which fields are meaningful: <c>"text"</c>/<c>"reasoning"</c> use <see cref="Text"/>
/// and <see cref="IsComplete"/>; <c>"tool"</c> uses the remaining fields.</summary>
public sealed class PersistedTurnItem
{
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public bool IsComplete { get; set; }
    public string? CallId { get; set; }
    public string? ToolName { get; set; }
    public string? ArgumentsJson { get; set; }
    public string? State { get; set; }
    public string? Result { get; set; }
}

/// <summary>Lightweight index entry, the only thing the sidebar/modal need to render a
/// list. Kept in <c>chat/index.json</c> so listing never parses full session files.</summary>
public sealed record ChatListItem(
    Guid Id,
    string Title,
    string? Branch,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int MessageCount);
