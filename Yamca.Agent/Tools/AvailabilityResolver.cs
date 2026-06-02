using Yamca.Agent.Settings;

namespace Yamca.Agent.Tools;

public sealed class AvailabilityResolver : IAvailabilityResolver
{
    private readonly IToolRegistry _tools;
    private readonly ISessionSettings _settings;

    public AvailabilityResolver(IToolRegistry tools, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(settings);
        _tools = tools;
        _settings = settings;
    }

    public Availability Resolve(string toolName)
    {
        var tool = _tools.Get(toolName);

        Availability requested;
        if (_settings.Project.Get(toolName)?.Availability is { } p) requested = p;
        else if (_settings.User.Get(toolName)?.Availability is { } g) requested = g;
        else requested = tool?.DefaultAvailability ?? Availability.Eager;

        if (tool is null) return requested;
        if (tool.MandatoryEager) return Availability.Eager;
        if (!tool.CanBeHidden && requested == Availability.Hidden) return tool.DefaultAvailability;
        return requested;
    }
}
