using Yamca.Agent.Settings.Persistence;
using Yamca.Agent.Tools;

namespace Yamca.Web.Services;

/// <summary>Hydrates and persists <see cref="SessionSettings"/> across its two tiers, both
/// of which now live on disk: the project tier under the repo's <c>.yamca</c>
/// (<see cref="ProjectSettingsStore"/>) and the user tier — which carries API keys and
/// isn't repo-scoped — under the OS per-user config directory
/// (<see cref="UserSettingsStore"/>). Pulls both on page load and writes the affected tier
/// back when <see cref="SessionSettings.Changed"/> fires.</summary>
public sealed class SettingsHydrator : IDisposable
{
    private readonly SessionSettings _settings;
    private readonly UserSettingsStore _userStore;
    private readonly ProjectSettingsStore _projectStore;
    private readonly IToolRegistry _tools;

    private bool _hydrated;
    private bool _persistOnChange;

    public SettingsHydrator(SessionSettings settings, UserSettingsStore userStore, ProjectSettingsStore projectStore, IToolRegistry tools)
    {
        _settings = settings;
        _userStore = userStore;
        _projectStore = projectStore;
        _tools = tools;
        _settings.Changed += OnChanged;
    }

    public bool IsHydrated => _hydrated;

    /// <summary>One-shot: read both tiers from disk and populate the session. Both reads are
    /// synchronous; the <see cref="Task"/> return is retained so existing call sites can keep
    /// awaiting it from <c>OnAfterRenderAsync</c> unchanged.</summary>
    public Task HydrateAsync()
    {
        if (_hydrated) return Task.CompletedTask;

        // Disable persistence while we're hydrating — the Changed events that fire
        // as we apply storage content would otherwise echo straight back to disk.
        _persistOnChange = false;
        try
        {
            _settings.HydrateUser(_userStore.Load());
            _settings.HydrateProject(_projectStore.Load());

            // Expand the User tier to an explicit value for every settings tool (no "inherit"
            // at the User level), backfilling tools added since the blob was last written.
            // Persist once if that changed anything, so the on-disk file matches what's shown.
            if (_settings.MaterializeUserToolDefaults(_tools.GetSettingsTools()))
                _userStore.Save(_settings.SerializeUser());
        }
        finally
        {
            _persistOnChange = true;
            _hydrated = true;
        }

        return Task.CompletedTask;
    }

    private void OnChanged(SettingsTier tier)
    {
        if (!_persistOnChange) return;

        if (tier == SettingsTier.Project)
            _projectStore.Save(_settings.SerializeProject());
        else
            _userStore.Save(_settings.SerializeUser());
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
    }
}
