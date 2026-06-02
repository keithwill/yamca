using Yamca.Agent.Settings.Persistence;

namespace Yamca.Web.Services;

/// <summary>Human-facing description of where each settings tier is persisted, surfaced in the
/// settings UI. Both tiers live on disk: the user tier (which carries API keys and isn't
/// repo-scoped) under the OS per-user config directory via
/// <see cref="UserSettingsStore"/>, and the project tier under the repository's
/// <c>.yamca</c> directory via <see cref="ProjectSettingsStore"/>.</summary>
public sealed class SettingsLocation
{
    /// <summary>Absolute path of the user-tier settings file, shown in the UI so users
    /// know where their endpoints/API keys are stored.</summary>
    public string UserFile => Path.Combine(UserSettingsStore.ResolveDefaultDirectory(), "user.json");

    /// <summary>Repository-relative path of the project-tier settings file.</summary>
    public string ProjectFile => ".yamca/project.json";
}
