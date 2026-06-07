using Yamca.Agent.Mcp;

namespace Yamca.Web.Services;

/// <summary>
/// Bridges <see cref="McpConfigFileStore"/> and <see cref="IMcpRegistry"/>: reads the
/// configured-server list from <c>mcp.json</c> in the per-user config directory on first
/// circuit, and pushes mutations back to disk and into the registry.
/// </summary>
public sealed class McpConfigStore
{
    private readonly McpConfigFileStore _storage;
    private readonly IMcpRegistry _registry;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<McpServerConfig> _configs = new();
    private bool _hydrated;

    public McpConfigStore(McpConfigFileStore storage, IMcpRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registry);
        _storage = storage;
        _registry = registry;
    }

    /// <summary>Absolute path of the on-disk server list file, surfaced in the UI.</summary>
    public string FilePath => _storage.FilePath;

    public IReadOnlyList<McpServerConfig> Configs
    {
        get { lock (_configs) return _configs.ToArray(); }
    }

    public bool Hydrated => _hydrated;

    /// <summary>Read the server list from mcp.json and push it into the
    /// process-wide registry. Idempotent across reloads in the same process:
    /// re-applying the same list is a no-op inside the registry's diff.</summary>
    public async Task HydrateAsync(CancellationToken cancellationToken = default)
    {
        bool seeded = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = _storage.Load();
            // A null blob means mcp.json doesn't exist yet (fresh install) — seed the default
            // servers. A present-but-empty "[]" is a deliberate user state (they removed every
            // server) and must be respected, so only the null case re-seeds.
            List<McpServerConfig> parsed;
            if (json is null)
            {
                parsed = McpServerConfigJson.DefaultConfigs().ToList();
                seeded = true;
            }
            else
            {
                parsed = McpServerConfigJson.DeserializeList(json).ToList();
            }
            ReplaceLocked(parsed);
            _hydrated = true;
        }
        finally
        {
            _gate.Release();
        }
        // Materialize the seed to disk so the user can see/edit the defaults and so the
        // null-means-seed check above fires exactly once across runs.
        if (seeded) await PersistAsync().ConfigureAwait(false);
        await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpConfigParseResult> AddAsync(string id, string json, CancellationToken cancellationToken = default)
    {
        var parsed = McpServerConfigJson.ParseSingle(json, overrideId: string.IsNullOrWhiteSpace(id) ? null : id.Trim());
        if (parsed.Config is null) return parsed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _configs.Where(c => !string.Equals(c.Id, parsed.Config.Id, StringComparison.Ordinal)).ToList();
            next.Add(parsed.Config);
            ReplaceLocked(next);
        }
        finally
        {
            _gate.Release();
        }
        await PersistAsync().ConfigureAwait(false);
        await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>Replace the config at <paramref name="originalId"/> with the
    /// parsed contents of <paramref name="json"/>. The id may change — the
    /// pasted JSON wins unless <paramref name="overrideId"/> is supplied.</summary>
    public async Task<McpConfigParseResult> ReplaceAsync(
        string originalId, string? overrideId, string json, CancellationToken cancellationToken = default)
    {
        var parsed = McpServerConfigJson.ParseSingle(
            json,
            overrideId: string.IsNullOrWhiteSpace(overrideId) ? null : overrideId.Trim());
        if (parsed.Config is null) return parsed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = new List<McpServerConfig>(_configs.Count);
            foreach (var c in _configs)
            {
                if (string.Equals(c.Id, originalId, StringComparison.Ordinal)) continue;
                if (string.Equals(c.Id, parsed.Config.Id, StringComparison.Ordinal)) continue;
                next.Add(c);
            }
            next.Add(parsed.Config);
            ReplaceLocked(next);
        }
        finally
        {
            _gate.Release();
        }
        await PersistAsync().ConfigureAwait(false);
        await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _configs.Where(c => !string.Equals(c.Id, id, StringComparison.Ordinal)).ToList();
            if (next.Count == _configs.Count) return;
            ReplaceLocked(next);
        }
        finally
        {
            _gate.Release();
        }
        await PersistAsync().ConfigureAwait(false);
        await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool dirty = false;
        try
        {
            var next = new List<McpServerConfig>(_configs.Count);
            foreach (var c in _configs)
            {
                if (string.Equals(c.Id, id, StringComparison.Ordinal) && c.Enabled != enabled)
                {
                    next.Add(c with { Enabled = enabled });
                    dirty = true;
                }
                else
                {
                    next.Add(c);
                }
            }
            if (!dirty) return;
            ReplaceLocked(next);
        }
        finally
        {
            _gate.Release();
        }
        if (dirty)
        {
            await PersistAsync().ConfigureAwait(false);
            await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Add back any shipped default server (<see cref="McpServerConfigJson.DefaultConfigs"/>)
    /// whose id is currently absent, leaving existing servers — including user-customized copies of a
    /// default — untouched. This is additive only: a default the user deleted reappears, but their edits
    /// to a default they kept are preserved. Returns the number of servers added (0 = nothing missing).</summary>
    public async Task<int> RestoreMissingDefaultsAsync(CancellationToken cancellationToken = default)
    {
        int added = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var present = new HashSet<string>(_configs.Select(c => c.Id), StringComparer.Ordinal);
            var missing = McpServerConfigJson.DefaultConfigs().Where(d => !present.Contains(d.Id)).ToList();
            if (missing.Count == 0) return 0;

            var next = new List<McpServerConfig>(_configs);
            next.AddRange(missing);
            ReplaceLocked(next);
            added = missing.Count;
        }
        finally
        {
            _gate.Release();
        }
        await PersistAsync().ConfigureAwait(false);
        await _registry.ReplaceAsync(_configs, cancellationToken).ConfigureAwait(false);
        return added;
    }

    private void ReplaceLocked(List<McpServerConfig> next) => _configs = next;

    private Task PersistAsync()
    {
        var json = McpServerConfigJson.SerializeList(_configs);
        _storage.Save(json);
        return Task.CompletedTask;
    }
}
