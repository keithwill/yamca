using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;

namespace Yamca.Web.Services;

/// <summary><see cref="IPermissionStore"/> adapter that writes approval decisions
/// back into the live <see cref="SessionSettings"/>. The settings object raises
/// <c>Changed</c>, which the chat circuit handler uses to persist to disk.</summary>
internal sealed class SessionSettingsPermissionStore : IPermissionStore
{
    private readonly SessionSettings _settings;

    public SessionSettingsPermissionStore(SessionSettings settings)
    {
        _settings = settings;
    }

    public void Persist(string toolName, PermissionLevel decision, ApprovalPersistence tier)
    {
        if (tier == ApprovalPersistence.None) return;

        var stier = tier == ApprovalPersistence.Project ? SettingsTier.Project : SettingsTier.Global;
        var map = stier == SettingsTier.Project ? _settings.Project : _settings.Global;
        var existing = map.Get(toolName) ?? new ToolPermissionSettings();
        _settings.SetToolEntry(stier, toolName, existing with { Permission = decision });
    }
}
