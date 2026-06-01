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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = _storage.Load();
            var parsed = McpServerConfigJson.DeserializeList(json).ToList();
            ReplaceLocked(parsed);
            _hydrated = true;
        }
        finally
        {
            _gate.Release();
        }
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

    private void ReplaceLocked(List<McpServerConfig> next) => _configs = next;

    private Task PersistAsync()
    {
        var json = McpServerConfigJson.SerializeList(_configs);
        _storage.Save(json);
        return Task.CompletedTask;
    }
}
