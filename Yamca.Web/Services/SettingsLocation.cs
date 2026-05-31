namespace Yamca.Web.Services;

/// <summary>Human-facing description of where each settings tier is persisted, surfaced in the
/// settings UI. The global tier lives in browser localStorage (it carries API keys and isn't
/// repo-scoped); the project tier lives on disk under the repository's <c>.yamca</c> directory
/// via <see cref="Yamca.Agent.Settings.Persistence.ProjectSettingsStore"/>.</summary>
public sealed class SettingsLocation
{
    /// <summary>localStorage key holding the global-tier blob.</summary>
    public const string GlobalStorageKey = "yamca.global";

    /// <summary>localStorage key holding the global-tier blob (instance accessor for Razor markup).</summary>
    public string GlobalKey => GlobalStorageKey;

    /// <summary>Repository-relative path of the project-tier settings file.</summary>
    public string ProjectFile => ".yamca/project.json";
}
