using Yamca.Agent.Tools;

namespace Yamca.Agent.Mcp;

/// <summary>
/// Process-scoped registry of MCP server connections. Implementations are
/// expected to be thread-safe.
/// </summary>
public interface IMcpRegistry
{
    /// <summary>All adapters across all currently <c>Ready</c> servers.</summary>
    IReadOnlyList<McpToolAdapter> Tools { get; }

    /// <summary>Live view of every configured server, in registration order.</summary>
    IReadOnlyList<McpServerConnection> Servers { get; }

    /// <summary>True once <see cref="ReplaceAsync"/> has been called at least once.
    /// Until then, <see cref="Tools"/> returns an empty list — the agent layer
    /// shouldn't pretend there are no MCP tools just because the web layer
    /// hasn't hydrated localStorage yet.</summary>
    bool Initialized { get; }

    /// <summary>Apply a new config set. Servers whose config is unchanged are
    /// reused; everything else is torn down and re-spawned.</summary>
    Task ReplaceAsync(IReadOnlyList<McpServerConfig> configs, CancellationToken cancellationToken = default);

    /// <summary>Tear down the named server and re-spawn it from its current
    /// config. No-op if no server has the given id. Returns true if a server
    /// was restarted.</summary>
    Task<bool> RestartAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Fires whenever the server list or any server's status changes.
    /// Used by the settings UI to refresh badges.</summary>
    event Action? Changed;
}
