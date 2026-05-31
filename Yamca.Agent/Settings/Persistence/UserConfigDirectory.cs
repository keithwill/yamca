namespace Yamca.Agent.Settings.Persistence;

/// <summary>Resolves the OS per-user config directory that yamca stores non-repo-scoped
/// state in (global settings, the MCP server list). Honors a <c>YAMCA_CONFIG_DIR</c>
/// override (power users + tests); otherwise uses
/// <see cref="Environment.SpecialFolder.ApplicationData"/> (which maps to <c>%APPDATA%</c>
/// on Windows and <c>$XDG_CONFIG_HOME</c>/<c>~/.config</c> on Unix), falling back to a
/// <c>.yamca</c> folder under the user profile when that resolves empty (rare, e.g. some
/// headless configurations).</summary>
public static class UserConfigDirectory
{
    public static string Resolve()
    {
        var overrideDir = Environment.GetEnvironmentVariable("YAMCA_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            return Path.Combine(appData, "yamca");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".yamca");
    }
}
