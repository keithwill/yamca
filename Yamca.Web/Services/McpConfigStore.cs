using Yamca.Agent.Mcp;

namespace Yamca.Web.Services;

/// <summary>
/// Bridges <see cref="LocalStorage"/> and <see cref="IMcpRegistry"/>: reads the
/// configured-server list from <c>yamca.mcp.servers</c> on first circuit, and
/// pushes mutations back to storage and into the registry.
/// </summary>
public sealed class McpConfigStore
{
    public const string StorageKey = "yamca.mcp.servers";

    private readonly LocalStorage _storage;
    private readonly IMcpRegistry _registry;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<McpServerConfig> _configs = new();
    private bool _hydrated;

    public McpConfigStore(LocalStorage storage, IMcpRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registry);
        _storage = storage;
        _registry = registry;
    }

    public IReadOnlyList<McpServerConfig> Configs
    {
        get { lock (_configs) return _configs.ToArray(); }
    }

    public bool Hydrated => _hydrated;

    /// <summary>Read the server list from localStorage and push it into the
    /// process-wide registry. Idempotent across reloads in the same process:
    /// re-applying the same list is a no-op inside the registry's diff.</summary>
    public async Task HydrateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await _storage.GetItemAsync(StorageKey).ConfigureAwait(false);
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

    private async Task PersistAsync()
    {
        var json = McpServerConfigJson.SerializeList(_configs);
        await _storage.SetItemAsync(StorageKey, json).ConfigureAwait(false);
    }
}
