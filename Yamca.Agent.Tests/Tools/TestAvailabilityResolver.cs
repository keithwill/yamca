using System.Collections.Generic;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

/// <summary>
/// Test double for <see cref="IAvailabilityResolver"/>. By default, returns each tool's
/// <see cref="ITool.DefaultAvailability"/> via the supplied registry (mimicking "no user
/// overrides set"). Tests can layer overrides on top via <see cref="Set"/>.
/// </summary>
internal sealed class TestAvailabilityResolver : IAvailabilityResolver
{
    private readonly IToolRegistry _registry;
    private readonly Dictionary<string, Availability> _overrides = new();

    public TestAvailabilityResolver(IToolRegistry registry)
    {
        _registry = registry;
    }

    public TestAvailabilityResolver Set(string name, Availability av)
    {
        _overrides[name] = av;
        return this;
    }

    public Availability Resolve(string toolName)
    {
        var tool = _registry.Get(toolName);
        Availability requested;
        if (_overrides.TryGetValue(toolName, out var ov)) requested = ov;
        else requested = tool?.DefaultAvailability ?? Availability.Eager;

        if (tool is null) return requested;
        if (tool.MandatoryEager) return Availability.Eager;
        if (!tool.CanBeHidden && requested == Availability.Hidden) return tool.DefaultAvailability;
        return requested;
    }
}
