namespace Yamca.Agent.Settings.Persistence;

/// <summary>Reads and writes the global-tier settings blob at
/// <c>&lt;userConfigDir&gt;/global.json</c> — by default under the OS per-user config
/// directory (<c>%APPDATA%\yamca</c> on Windows, <c>$XDG_CONFIG_HOME/yamca</c> or
/// <c>~/.config/yamca</c> on Linux/macOS). The global tier holds API keys and isn't
/// repo-scoped, so it lives in the user profile rather than the working directory: this
/// keeps secrets out of the LLM-reachable workspace while still surviving across ports,
/// browsers, and cleared site data.
///
/// Deliberately dumb, exactly like <see cref="ProjectSettingsStore"/>: the store shuttles
/// an opaque JSON string to and from disk and never inspects its shape —
/// <c>SessionSettings.SerializeGlobal()</c>/<c>HydrateGlobal()</c> remain the single source
/// of truth for the blob contract.
///
/// Always enabled (no git-repo gate — global state isn't tied to any repository). Reads and
/// writes are serialized through a lock, fine for the single-user usage here. Note that a
/// single shared file means two concurrently-running yamca processes are last-write-wins;
/// the atomic temp-file-then-rename in <see cref="Save"/> prevents corruption, but the last
/// save simply wins. localStorage used to isolate instances per-origin (per-port); that
/// isolation is intentionally gone in exchange for port-independent persistence.</summary>
public sealed class GlobalSettingsStore
{
    private readonly string _configDirectory;
    private readonly object _gate = new();

    public GlobalSettingsStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _configDirectory = configDirectory;
    }

    private string SettingsPath => Path.Combine(_configDirectory, "global.json");

    /// <summary>Resolve the default per-user config directory for global settings — see
    /// <see cref="UserConfigDirectory.Resolve"/>.</summary>
    public static string ResolveDefaultDirectory() => UserConfigDirectory.Resolve();

    /// <summary>Return the raw global-settings blob, or null when missing or unreadable.
    /// The caller feeds this straight to <c>SessionSettings.HydrateGlobal(json)</c>.</summary>
    public string? Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Persist the global-settings blob, overwriting any existing file.</summary>
    public void Save(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_configDirectory);
                WriteAtomic(SettingsPath, json);
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
