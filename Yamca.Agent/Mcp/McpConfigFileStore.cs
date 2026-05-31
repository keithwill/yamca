using Yamca.Agent.Settings.Persistence;

namespace Yamca.Agent.Mcp;

/// <summary>Reads and writes the MCP server list blob at <c>&lt;userConfigDir&gt;/mcp.json</c>,
/// alongside the global settings file. The server list isn't repo-scoped (it's process-wide,
/// shared by every chat session) and its configs can carry secrets in env vars, so it lives in
/// the OS per-user config directory rather than the LLM-reachable workspace.
///
/// Deliberately dumb, exactly like <see cref="GlobalSettingsStore"/> and
/// <see cref="ProjectSettingsStore"/>: the store shuttles an opaque JSON string to and from
/// disk and never inspects its shape — <see cref="McpServerConfigJson"/> remains the single
/// source of truth for the blob contract.
///
/// Always enabled. Reads/writes are serialized through a lock; a single shared file means two
/// concurrently-running yamca processes are last-write-wins, with the atomic temp-file-then-
/// rename in <see cref="Save"/> guarding against corruption.</summary>
public sealed class McpConfigFileStore
{
    private readonly string _configDirectory;
    private readonly object _gate = new();

    public McpConfigFileStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _configDirectory = configDirectory;
    }

    /// <summary>Absolute path of the MCP server list file, surfaced in the UI.</summary>
    public string FilePath => Path.Combine(_configDirectory, "mcp.json");

    /// <summary>Return the raw MCP server list blob, or null when missing or unreadable.
    /// The caller feeds this straight to <see cref="McpServerConfigJson.DeserializeList"/>.</summary>
    public string? Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Persist the MCP server list blob, overwriting any existing file.</summary>
    public void Save(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_configDirectory);
                WriteAtomic(FilePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort: a transient write failure shouldn't crash the circuit
            }
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
