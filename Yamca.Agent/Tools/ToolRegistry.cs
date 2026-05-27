using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byName;
    private readonly List<ITool> _ordered;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _ordered = tools.ToList();
        _byName = new Dictionary<string, ITool>(StringComparer.Ordinal);

        foreach (var tool in _ordered)
        {
            if (!_byName.TryAdd(tool.Name, tool))
                throw new ArgumentException($"Duplicate tool name '{tool.Name}'.", nameof(tools));
        }
    }

    public IReadOnlyList<ITool> Tools => _ordered;

    public ITool? Get(string name) =>
        name is not null && _byName.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<ChatTool> GetChatTools(LoadedToolSet loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return _ordered
            .Where(t => t.ExposedToLlm && (!t.Deferred || loaded.Contains(t.Name)))
            .Select(t => new ChatTool(t.Name, t.Description, t.ParametersSchema))
            .ToList();
    }

    public IReadOnlyList<ITool> GetDeferredTools() =>
        _ordered.Where(t => t.ExposedToLlm && t.Deferred).ToList();

    public IReadOnlyList<ITool> GetSettingsTools() =>
        _ordered.Where(t => t.ExposedInSettings).ToList();
}
