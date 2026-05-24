using Microsoft.JSInterop;

namespace Yamca.Web.Services;

/// <summary>Bridges <see cref="SessionSettings"/> and <see cref="LocalStorage"/>:
/// pulls both tiers on page load, and writes the affected tier back when
/// <see cref="SessionSettings.Changed"/> fires.</summary>
public sealed class SettingsHydrator : IDisposable
{
    private readonly SessionSettings _settings;
    private readonly LocalStorage _storage;
    private readonly WorkspaceKey _keys;

    private bool _hydrated;
    private bool _persistOnChange;

    public SettingsHydrator(SessionSettings settings, LocalStorage storage, WorkspaceKey keys)
    {
        _settings = settings;
        _storage = storage;
        _keys = keys;
        _settings.Changed += OnChanged;
    }

    public bool IsHydrated => _hydrated;

    /// <summary>One-shot: read both blobs from localStorage and populate the session.
    /// Must be called from a component after first render (when JS interop is available).</summary>
    public async Task HydrateAsync()
    {
        if (_hydrated) return;

        // Disable persistence while we're hydrating — the Changed events that fire
        // as we apply storage content would otherwise echo straight back to storage.
        _persistOnChange = false;
        try
        {
            var globalJson = await _storage.GetItemAsync(_keys.GlobalKey).ConfigureAwait(false);
            _settings.HydrateGlobal(globalJson);

            var projectJson = await _storage.GetItemAsync(_keys.ProjectKey).ConfigureAwait(false);
            _settings.HydrateProject(projectJson);
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
        _ = PersistAsync(tier);
    }

    private async Task PersistAsync(SettingsTier tier)
    {
        var (key, payload) = tier == SettingsTier.Project
            ? (_keys.ProjectKey, _settings.SerializeProject())
            : (_keys.GlobalKey, _settings.SerializeGlobal());

        try
        {
            await _storage.SetItemAsync(key, payload).ConfigureAwait(false);
        }
        catch (JSDisconnectedException) { /* circuit gone, nothing we can do */ }
        catch (TaskCanceledException)   { /* same */ }
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
    }
}
