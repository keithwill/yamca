using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Mcp;

/// <summary>
/// Owns one connected <see cref="McpClient"/> plus its adapter list. Lifecycle
/// is fully owned by <see cref="McpRegistry"/>; instances are not reused after
/// <see cref="DisposeAsync"/>.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private McpClient? _client;
    private IReadOnlyList<McpToolAdapter> _adapters = Array.Empty<McpToolAdapter>();
    private McpServerStatus _status;
    private string? _failureMessage;
    private int _disposed;

    public McpServerConnection(McpServerConfig config, McpServerLogBuffer? logBuffer = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Log = logBuffer ?? new McpServerLogBuffer();
        _status = config.Enabled ? McpServerStatus.Connecting : McpServerStatus.Disabled;
    }

    public McpServerConfig Config { get; }
    public McpServerLogBuffer Log { get; }

    public McpServerStatus Status { get { lock (_gate) return _status; } }
    public string? FailureMessage { get { lock (_gate) return _failureMessage; } }

    public IReadOnlyList<McpToolAdapter> Adapters
    {
        get { lock (_gate) return _adapters; }
    }

    public event Action? StateChanged;

    /// <summary>Connect to the server, handshake, and enumerate tools. Safe to
    /// call once. On failure the connection transitions to <see cref="McpServerStatus.Failed"/>
    /// and the exception is captured (not rethrown) so one bad server can't
    /// take down the rest.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Config.Enabled)
        {
            SetState(McpServerStatus.Disabled, null);
            return;
        }

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(DefaultStartupTimeout);

        StdioClientTransport transport;
        try
        {
            transport = BuildTransport();
        }
        catch (Exception ex)
        {
            Log.Append("yamca", $"Failed to build transport: {ex.Message}");
            SetState(McpServerStatus.Failed, ex.Message);
            return;
        }

        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, startupCts.Token)
                                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Append("yamca", $"Failed to start server: {ex.Message}");
            SetState(McpServerStatus.Failed, ex.Message);
            return;
        }

        IList<McpClientTool> tools;
        try
        {
            tools = await client.ListToolsAsync(options: null, startupCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Append("yamca", $"tools/list failed: {ex.Message}");
            await SafeDisposeAsync(client).ConfigureAwait(false);
            SetState(McpServerStatus.Failed, ex.Message);
            return;
        }

        var adapters = tools
            .Select(t => new McpToolAdapter(Config.Id, t, Log, DefaultCallTimeout))
            .ToArray();

        lock (_gate)
        {
            _client = client;
            _adapters = adapters;
            _status = McpServerStatus.Ready;
            _failureMessage = null;
        }
        RaiseStateChanged();
    }

    private StdioClientTransport BuildTransport()
    {
        // On Windows the SDK calls Process.Start(command, ...) directly, which
        // does NOT consult PATHEXT. So a bare "npx" / "uvx" / "deno" — the form
        // every MCP server README ships with — would fail to launch even when
        // those commands resolve fine from PowerShell. Resolve them against
        // PATH+PATHEXT ourselves so paste-from-README works on Windows.
        var resolvedCommand = ResolveCommand(Config.Stdio.Command);

        var options = new StdioClientTransportOptions
        {
            Name = Config.Id,
            Command = resolvedCommand,
            Arguments = Config.Stdio.Args.Count == 0 ? null : Config.Stdio.Args.ToList(),
            WorkingDirectory = Config.Stdio.WorkingDirectory,
            EnvironmentVariables = Config.Stdio.Env is null
                ? null
                : Config.Stdio.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal),
            // Pipe stderr lines into the per-server log so the settings UI can
            // surface them when something goes wrong.
            StandardErrorLines = line => Log.Append("stderr", line),
        };
        return new StdioClientTransport(options);
    }

    private string ResolveCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return command;

        // Absolute / explicit relative paths: trust the user.
        if (Path.IsPathRooted(command)) return command;
        if (command.Contains('/', StringComparison.Ordinal) || command.Contains('\\', StringComparison.Ordinal))
            return command;

        var resolver = new InterpreterResolver();
        var resolved = resolver.Resolve(new[] { command });
        if (resolved is null)
        {
            Log.Append("yamca", $"Command '{command}' was not found on PATH. " +
                "Falling back to the literal value; the SDK may not be able to launch it on Windows.");
            return command;
        }

        if (!string.Equals(resolved, command, StringComparison.OrdinalIgnoreCase))
            Log.Append("yamca", $"Resolved '{command}' on PATH to '{resolved}'.");
        return resolved;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        McpClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _adapters = Array.Empty<McpToolAdapter>();
            _status = McpServerStatus.Disabled;
        }
        await SafeDisposeAsync(client).ConfigureAwait(false);
        RaiseStateChanged();
    }

    private static async Task SafeDisposeAsync(McpClient? client)
    {
        if (client is null) return;
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { /* shutdown errors are non-actionable */ }
    }

    private void SetState(McpServerStatus status, string? failure)
    {
        lock (_gate)
        {
            _status = status;
            _failureMessage = failure;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        var handler = StateChanged;
        if (handler is null) return;
        try { handler(); }
        catch { /* listener faults shouldn't take down the registry */ }
    }
}
