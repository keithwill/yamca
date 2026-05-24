using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Support;

/// <summary>Test-only <see cref="IPermissionStore"/> that mutates a shared
/// <see cref="InMemorySessionSettings"/> so subsequent permission resolutions
/// see the persisted choice.</summary>
internal sealed class InMemoryPermissionStore : IPermissionStore
{
    private readonly InMemorySessionSettings _settings;
    public List<(string Tool, PermissionLevel Level, ApprovalPersistence Tier)> Writes { get; } = new();

    public InMemoryPermissionStore(InMemorySessionSettings settings)
    {
        _settings = settings;
    }

    public void Persist(string toolName, PermissionLevel decision, ApprovalPersistence tier)
    {
        Writes.Add((toolName, decision, tier));

        if (tier == ApprovalPersistence.None) return;

        var source = tier == ApprovalPersistence.Project ? _settings.Project : _settings.Global;
        var next = new Dictionary<string, ToolPermissionSettings>(source.Entries, StringComparer.Ordinal);
        var existing = source.Get(toolName) ?? new ToolPermissionSettings();
        next[toolName] = existing with { Permission = decision };
        var map = new ToolSettingsMap(next);

        if (tier == ApprovalPersistence.Project) _settings.Project = map;
        else _settings.Global = map;
    }
}
