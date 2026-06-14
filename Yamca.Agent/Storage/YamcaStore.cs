using VestPocket;
using Yamca.Agent.Board;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Storage;

/// <summary>Owns the single <see cref="VestPocketStore"/> backing all of yamca's local document state,
/// at <c>&lt;RepositoryRoot&gt;/.yamca/yamca.db</c>. Features layer on top via path-like key prefixes
/// (e.g. the dev board uses <c>/board/card/…</c> and <c>/board/column/…</c>); the file name is
/// deliberately generic so future data can share the store.
///
/// The store is opened lazily behind an async gate so callers needn't depend on host startup ordering,
/// and is cached for the process lifetime (it keeps all records in memory, synced to the append-only
/// file). VestPocket is itself thread-safe; this wrapper only coordinates one-time open/close.</summary>
public sealed class YamcaStore : IAsyncDisposable
{
    private readonly string? _filePath;          // null ⇒ in-memory store (tests)
    private readonly string? _repositoryRoot;    // for the managed .yamca/.gitignore; null when in-memory
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VestPocketStore? _store;

    public YamcaStore(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _repositoryRoot = workspace.RepositoryRoot;
        _filePath = Path.Combine(_repositoryRoot, ".yamca", "yamca.db");
    }

    // Test seam: a null file path opens a pure in-memory store (no disk, no gitignore).
    internal YamcaStore(string? filePath, string? repositoryRoot = null)
    {
        _filePath = filePath;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>Resolve the opened store, creating <c>.yamca</c> (and its managed gitignore) and opening
    /// the VestPocket file on first use. Idempotent and cached after first success.</summary>
    public async Task<VestPocketStore> GetAsync(CancellationToken ct)
    {
        if (_store is not null) return _store;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store is not null) return _store;

            if (_filePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                if (_repositoryRoot is not null)
                    WorkspaceScaffold.EnsureGitignore(_repositoryRoot);
            }

            var options = new VestPocketOptions
            {
                FilePath = _filePath,
                JsonSerializerContext = YamcaStoreJsonContext.Default,
                Durability = VestPocketDurability.FlushOnDelay,
            };
            options.AddType<ColumnRecord>();
            options.AddType<CardRecord>();
            options.AddType<CardCounter>();

            var store = new VestPocketStore(options);
            await store.OpenAsync(ct).ConfigureAwait(false);
            _store = store;
            return store;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Flush and close the store so pending transactions are durably written. Safe to call
    /// when the store was never opened.</summary>
    public async Task CloseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store is not null)
            {
                await _store.Close(ct).ConfigureAwait(false);
                _store = null;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
