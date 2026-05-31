using Microsoft.JSInterop;
using Yamca.Agent.Settings.Persistence;

namespace Yamca.Web.Services;

/// <summary>Hydrates and persists <see cref="SessionSettings"/> across its two tiers:
/// the project tier lives on disk (<see cref="ProjectSettingsStore"/>, under the repo's
/// <c>.yamca</c>) and the global tier — which carries API keys and isn't repo-scoped —
/// stays in browser localStorage. Pulls both on page load and writes the affected tier
/// back when <see cref="SessionSettings.Changed"/> fires.</summary>
public sealed class SettingsHydrator : IDisposable
{
    private const string GlobalKey = SettingsLocation.GlobalStorageKey;

    private readonly SessionSettings _settings;
    private readonly LocalStorage _storage;
    private readonly ProjectSettingsStore _projectStore;

    private bool _hydrated;
    private bool _persistOnChange;

    public SettingsHydrator(SessionSettings settings, LocalStorage storage, ProjectSettingsStore projectStore)
    {
        _settings = settings;
        _storage = storage;
        _projectStore = projectStore;
        _settings.Changed += OnChanged;
    }

    public bool IsHydrated => _hydrated;

    /// <summary>One-shot: read the global blob from localStorage and the project blob from
    /// disk, then populate the session. Must be called from a component after first render
    /// (when JS interop is available for the global read).</summary>
    public async Task HydrateAsync()
    {
        if (_hydrated) return;

        // Disable persistence while we're hydrating — the Changed events that fire
        // as we apply storage content would otherwise echo straight back to storage.
        _persistOnChange = false;
        try
        {
            var globalJson = await _storage.GetItemAsync(GlobalKey).ConfigureAwait(false);
            _settings.HydrateGlobal(globalJson);

            _settings.HydrateProject(_projectStore.Load());
        }
        finally
        {
            _persistOnChange = true;
            _hydrated = true;
        }
    }

    private void OnChanged(SettingsTier tier)
    {
        if (!_persistOnChange) return;

        if (tier == SettingsTier.Project)
            _projectStore.Save(_settings.SerializeProject());
        else
            _ = PersistGlobalAsync();
    }

    private async Task PersistGlobalAsync()
    {
        try
        {
            await _storage.SetItemAsync(GlobalKey, _settings.SerializeGlobal()).ConfigureAwait(false);
        }
        catch (JSDisconnectedException) { /* circuit gone, nothing we can do */ }
        catch (TaskCanceledException)   { /* same */ }
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
    }
}
