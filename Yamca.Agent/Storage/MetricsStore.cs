using VestPocket;
using Yamca.Agent.Metrics;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Storage;

/// <summary>Owns the dedicated <see cref="VestPocketStore"/> backing throughput metrics, at
/// <c>&lt;RepositoryRoot&gt;/.yamca/metrics.db</c>. Kept separate from the shared
/// <see cref="YamcaStore"/> (board / chat state) on purpose: metrics are high-volume, append-only
/// machine exhaust with a disposable lifecycle, whereas <c>yamca.db</c> holds small, curated,
/// durable state. A separate file keeps that store lean and makes "clear / truncate metrics" a
/// safe, store-local operation that can never touch board or card data.
///
/// Records are keyed <c>/metric/{id}</c>. Like <see cref="YamcaStore"/> the file is opened lazily
/// behind an async gate and cached for the process lifetime.</summary>
public sealed class MetricsStore : IAsyncDisposable
{
    internal const string KeyPrefix = "/metric/";

    private readonly string? _filePath;          // null ⇒ in-memory store (tests)
    private readonly string? _repositoryRoot;    // for the managed .yamca/.gitignore; null when in-memory
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VestPocketStore? _store;

    public MetricsStore(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _repositoryRoot = workspace.RepositoryRoot;
        _filePath = Path.Combine(_repositoryRoot, ".yamca", "metrics.db");
    }

    // Test seam: a null file path opens a pure in-memory store (no disk, no gitignore).
    internal MetricsStore(string? filePath, string? repositoryRoot = null)
    {
        _filePath = filePath;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>Current on-disk size of the metrics file in bytes (0 for an in-memory store or
    /// before the file exists). Surfaced in the dashboard's summary strip.</summary>
    public long FileSizeBytes =>
        _filePath is not null && File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;

    /// <summary>Resolve the opened store, creating <c>.yamca</c> (and its managed gitignore) and
    /// opening the VestPocket file on first use. Idempotent and cached after first success.</summary>
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
                JsonSerializerContext = MetricsStoreJsonContext.Default,
                Durability = VestPocketDurability.FlushOnDelay,
            };
            options.AddType<TurnMetric>();

            var store = new VestPocketStore(options);
            await store.OpenAsync(ct).ConfigureAwait(false);
            _store = store;
            return store;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persist a batch of samples in one transaction.</summary>
    public async Task RecordManyAsync(IReadOnlyList<TurnMetric> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;
        var store = await GetAsync(ct).ConfigureAwait(false);
        var batch = new Kvp[metrics.Count];
        for (var i = 0; i < metrics.Count; i++)
            batch[i] = new Kvp(KeyPrefix + metrics[i].Id, metrics[i]);
        await store.Save(batch).ConfigureAwait(false);
    }

    /// <summary>All recorded samples, in no particular order (callers sort/bucket as needed).</summary>
    public async Task<IReadOnlyList<TurnMetric>> QueryAsync(CancellationToken ct)
    {
        var store = await GetAsync(ct).ConfigureAwait(false);
        var list = new List<TurnMetric>();
        foreach (var kv in store.GetByPrefix(KeyPrefix))
            if (kv.Value is TurnMetric m) list.Add(m);
        return list;
    }

    /// <summary>Enforce retention: drop samples older than <paramref name="maxAge"/> and, beyond
    /// that, the oldest samples in excess of <paramref name="keepMax"/>. Returns the number deleted.
    /// Reclaims on-disk space (a store rewrite) only when a large number were removed, to avoid
    /// churning the file on every routine prune.</summary>
    public async Task<int> PruneAsync(int keepMax, TimeSpan? maxAge, CancellationToken ct)
    {
        var store = await GetAsync(ct).ConfigureAwait(false);

        var all = new List<(string Key, DateTimeOffset Ts)>();
        foreach (var kv in store.GetByPrefix(KeyPrefix))
            if (kv.Value is TurnMetric m) all.Add((kv.Key, m.TimestampUtc));

        // Oldest first, so the over-cap overflow is the head of the list.
        all.Sort((a, b) => a.Ts.CompareTo(b.Ts));

        var cutoff = maxAge is { } age ? DateTimeOffset.UtcNow - age : (DateTimeOffset?)null;
        var overCap = Math.Max(0, all.Count - keepMax);

        var toDelete = new List<string>();
        for (var i = 0; i < all.Count; i++)
        {
            var tooOld = cutoff is { } c && all[i].Ts < c;
            var overflow = i < overCap;
            if (tooOld || overflow) toDelete.Add(all[i].Key);
        }

        if (toDelete.Count == 0) return 0;

        var batch = new Kvp[toDelete.Count];
        for (var i = 0; i < toDelete.Count; i++)
            batch[i] = new Kvp(toDelete[i], null!); // a null value deletes the key in VestPocket
        await store.Save(batch).ConfigureAwait(false);

        if (toDelete.Count > 1000)
            await store.ForceMaintenance().ConfigureAwait(false);

        return toDelete.Count;
    }

    /// <summary>Wipe every sample. Because this store is dedicated to metrics, the whole-store
    /// clear (which also reclaims disk) is exactly the intended "truncate my metrics" operation —
    /// it can never affect board or chat state, which live in the separate <see cref="YamcaStore"/>.</summary>
    public async Task ClearAllAsync(CancellationToken ct)
    {
        var store = await GetAsync(ct).ConfigureAwait(false);
        store.Clear();
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
