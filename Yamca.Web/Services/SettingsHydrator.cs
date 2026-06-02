using Yamca.Agent.Settings.Persistence;

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

    private bool _hydrated;
    private bool _persistOnChange;

    public SettingsHydrator(SessionSettings settings, UserSettingsStore userStore, ProjectSettingsStore projectStore)
    {
        _settings = settings;
        _userStore = userStore;
        _projectStore = projectStore;
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
