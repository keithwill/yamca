using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly List<ITool> _static;
    private readonly Dictionary<string, ITool> _staticByName;
    private readonly IReadOnlyList<IDynamicToolSource> _dynamicSources;

    public ToolRegistry(IEnumerable<ITool> tools)
        : this(tools, Array.Empty<IDynamicToolSource>())
    {
    }

    public ToolRegistry(IEnumerable<ITool> tools, IEnumerable<IDynamicToolSource> dynamicSources)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(dynamicSources);

        _static = tools.ToList();
        _staticByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in _static)
        {
            if (!_staticByName.TryAdd(tool.Name, tool))
                throw new ArgumentException($"Duplicate tool name '{tool.Name}'.", nameof(tools));
        }
        _dynamicSources = dynamicSources.ToList();
    }

    /// <summary>Snapshot of static + dynamic tools at call time. Dynamic tools
    /// follow static tools and any duplicate name (dynamic-vs-static or
    /// dynamic-vs-dynamic) is dropped to keep the registry single-valued.</summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            var dynamicTools = CollectDynamic(out _);
            if (dynamicTools.Count == 0) return _static.ToList();
            var combined = new List<ITool>(_static.Count + dynamicTools.Count);
            combined.AddRange(_static);
            combined.AddRange(dynamicTools);
            return combined;
        }
    }

    public ITool? Get(string name)
    {
        if (name is null) return null;
        if (_staticByName.TryGetValue(name, out var tool)) return tool;

        foreach (var source in _dynamicSources)
        {
            foreach (var dyn in source.CurrentTools)
                if (string.Equals(dyn.Name, name, StringComparison.Ordinal)) return dyn;
        }
        return null;
    }

    public IReadOnlyList<ChatTool> GetChatTools(LoadedToolSet loaded, IAvailabilityResolver availability)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(availability);
        var dynamicTools = CollectDynamic(out var dynamicNames);
        var result = new List<ChatTool>();
        foreach (var t in EnumerateAll(dynamicTools, dynamicNames))
        {
            if (!t.ExposedToLlm) continue;
            var av = availability.Resolve(t.Name);
            if (av == Availability.Hidden) continue;
            if (av == Availability.Deferred && !loaded.Contains(t.Name)) continue;
            result.Add(new ChatTool(t.Name, t.Description, t.ParametersSchema));
        }
        return result;
    }

    public IReadOnlyList<ITool> GetDeferredTools(IAvailabilityResolver availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var dynamicTools = CollectDynamic(out var dynamicNames);
        return EnumerateAll(dynamicTools, dynamicNames)
            .Where(t => t.ExposedToLlm && availability.Resolve(t.Name) == Availability.Deferred)
            .ToList();
    }

    public IReadOnlyList<ITool> GetSettingsTools()
    {
        var dynamicTools = CollectDynamic(out var dynamicNames);
        return EnumerateAll(dynamicTools, dynamicNames)
            .Where(t => t.ExposedInSettings)
            .ToList();
    }

    private IEnumerable<ITool> EnumerateAll(List<ITool> dynamicTools, HashSet<string> dynamicNames)
    {
        foreach (var t in _static) yield return t;
        foreach (var t in dynamicTools) yield return t;
    }

    private List<ITool> CollectDynamic(out HashSet<string> seen)
    {
        seen = new HashSet<string>(StringComparer.Ordinal);
        if (_dynamicSources.Count == 0) return new List<ITool>();

        var list = new List<ITool>();
        foreach (var source in _dynamicSources)
        {
            foreach (var tool in source.CurrentTools)
            {
                // Static tools win over dynamic-with-the-same-name, and we
                // de-dupe dynamic-vs-dynamic too so the chat tool list never
                // contains a name twice (the LLM would reject that).
                if (_staticByName.ContainsKey(tool.Name)) continue;
                if (!seen.Add(tool.Name)) continue;
                list.Add(tool);
            }
        }
        return list;
    }
}
