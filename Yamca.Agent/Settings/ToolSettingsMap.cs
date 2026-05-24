namespace Yamca.Agent.Settings;

public sealed class ToolSettingsMap
{
    public static ToolSettingsMap Empty { get; } = new();

    private readonly Dictionary<string, ToolPermissionSettings> _entries;

    public ToolSettingsMap()
        : this(new Dictionary<string, ToolPermissionSettings>(StringComparer.Ordinal))
    {
    }

    public ToolSettingsMap(IEnumerable<KeyValuePair<string, ToolPermissionSettings>> entries)
        : this(new Dictionary<string, ToolPermissionSettings>(entries, StringComparer.Ordinal))
    {
    }

    private ToolSettingsMap(Dictionary<string, ToolPermissionSettings> entries)
    {
        _entries = entries;
    }

    public ToolPermissionSettings? Get(string toolName) =>
        _entries.TryGetValue(toolName, out var entry) ? entry : null;

    public IReadOnlyDictionary<string, ToolPermissionSettings> Entries => _entries;
}
