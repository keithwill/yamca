using Yamca.Agent.Tools;

namespace Yamca.Agent.Mcp;

/// <summary>
/// Process-scoped MCP registry. Holds one <see cref="McpServerConnection"/> per
/// configured server and exposes the union of their tool adapters. Reload is
/// diff-based: only entries whose config actually changed are torn down.
/// </summary>
public sealed class McpRegistry : IMcpRegistry, IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _readGate = new();
    private List<McpServerConnection> _connections = new();
    private bool _disposed;
    private bool _initialized;

    public event Action? Changed;

    public bool Initialized { get { lock (_readGate) return _initialized; } }

    public IReadOnlyList<McpServerConnection> Servers
    {
        get { lock (_readGate) return _connections.ToArray(); }
    }

    public IReadOnlyList<McpToolAdapter> Tools
    {
        get
        {
            lock (_readGate)
            {
                if (!_initialized) return Array.Empty<McpToolAdapter>();
                var list = new List<McpToolAdapter>();
                foreach (var c in _connections)
                {
                    if (c.Status != McpServerStatus.Ready) continue;
                    list.AddRange(c.Adapters);
                }
                return list;
            }
        }
    }

    public async Task ReplaceAsync(IReadOnlyList<McpServerConfig> configs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configs);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            List<McpServerConnection> existing;
            lock (_readGate) existing = _connections.ToList();

            var existingById = existing.ToDictionary(c => c.Config.Id, StringComparer.Ordinal);
            var nextById = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
            foreach (var cfg in configs)
            {
                if (!nextById.ContainsKey(cfg.Id)) nextById[cfg.Id] = cfg;
            }

            var toDispose = new List<McpServerConnection>();
            var next = new List<McpServerConnection>(configs.Count);
            var newOnes = new List<McpServerConnection>();

            foreach (var cfg in configs)
            {
                if (!nextById.TryGetValue(cfg.Id, out var winning) || !ReferenceEquals(winning, cfg))
                    continue; // duplicate id — keep only the first

                if (existingById.TryGetValue(cfg.Id, out var current) && SameConfig(current.Config, cfg))
                {
                    next.Add(current);
                    existingById.Remove(cfg.Id);
                }
                else
                {
                    var fresh = new McpServerConnection(cfg);
                    fresh.StateChanged += OnConnectionStateChanged;
                    next.Add(fresh);
                    newOnes.Add(fresh);
                }
            }

            // Anything left in existingById was removed from the new config set.
            toDispose.AddRange(existingById.Values);

            lock (_readGate)
            {
                _connections = next;
                _initialized = true;
            }

            // Tear down removed servers in parallel — independent of each other.
            await Task.WhenAll(toDispose.Select(async c =>
            {
                c.StateChanged -= OnConnectionStateChanged;
                await c.DisposeAsync().ConfigureAwait(false);
            })).ConfigureAwait(false);

            // Spin up newly-added servers in the background. We don't await
            // here because:
            //   1. A slow server shouldn't block the settings page roundtrip.
            //   2. Tool adapters become visible via the StateChanged event,
            //      which the UI already listens for.
            foreach (var fresh in newOnes)
            {
                _ = Task.Run(() => fresh.ConnectAsync(CancellationToken.None), CancellationToken.None);
            }

            RaiseChanged();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            List<McpServerConnection> snapshot;
            lock (_readGate)
            {
                snapshot = _connections;
                _connections = new List<McpServerConnection>();
            }
            foreach (var c in snapshot) c.StateChanged -= OnConnectionStateChanged;
            await Task.WhenAll(snapshot.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void OnConnectionStateChanged() => RaiseChanged();

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(); }
        catch { /* listener faults shouldn't take down the registry */ }
    }

    private static bool SameConfig(McpServerConfig a, McpServerConfig b)
    {
        if (a.Id != b.Id) return false;
        if (a.Enabled != b.Enabled) return false;
        if (a.CallTimeoutSeconds != b.CallTimeoutSeconds) return false;
        if (a.TransportKind != b.TransportKind) return false;
        if (a.Stdio is not null)
        {
            if (b.Stdio is null) return false;
            if (a.Stdio.Command != b.Stdio.Command) return false;
            if (a.Stdio.WorkingDirectory != b.Stdio.WorkingDirectory) return false;
            if (!a.Stdio.Args.SequenceEqual(b.Stdio.Args, StringComparer.Ordinal)) return false;
            if (!DictEquals(a.Stdio.Env, b.Stdio.Env)) return false;
        }
        if (a.Http is not null)
        {
            if (b.Http is null) return false;
            if (a.Http.Url != b.Http.Url) return false;
            if (!DictEquals(a.Http.Headers, b.Http.Headers)) return false;
        }
        return true;
    }

    private static bool DictEquals(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        var aCount = a?.Count ?? 0;
        var bCount = b?.Count ?? 0;
        if (aCount != bCount) return false;
        if (aCount == 0) return true;
        foreach (var kv in a!)
        {
            if (!b!.TryGetValue(kv.Key, out var v)) return false;
            if (!string.Equals(v, kv.Value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Tear down the named server (if present) and re-spawn it from
    /// its current config. Used by the settings UI's "test connection" button
    /// to retry after a transient failure without round-tripping the on-disk config.</summary>
    public async Task<bool> RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id)) return false;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        McpServerConnection? toDispose = null;
        McpServerConnection? fresh = null;
        try
        {
            if (_disposed) return false;

            List<McpServerConnection> existing;
            lock (_readGate) existing = _connections.ToList();

            var idx = existing.FindIndex(c => string.Equals(c.Config.Id, id, StringComparison.Ordinal));
            if (idx < 0) return false;

            toDispose = existing[idx];
            fresh = new McpServerConnection(toDispose.Config);
            fresh.StateChanged += OnConnectionStateChanged;
            existing[idx] = fresh;

            lock (_readGate) _connections = existing;
        }
        finally
        {
            _writeGate.Release();
        }

        if (toDispose is not null)
        {
            toDispose.StateChanged -= OnConnectionStateChanged;
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        if (fresh is not null)
        {
            _ = Task.Run(() => fresh.ConnectAsync(CancellationToken.None), CancellationToken.None);
        }

        RaiseChanged();
        return true;
    }
}
