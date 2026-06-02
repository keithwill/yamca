using Yamca.Agent.Settings;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Permissions;

public sealed class PermissionResolver : IPermissionResolver
{
    private readonly IToolRegistry _tools;
    private readonly ISessionSettings _settings;

    public PermissionResolver(IToolRegistry tools, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(settings);
        _tools = tools;
        _settings = settings;
    }

    public PermissionLevel Resolve(string toolName)
    {
        if (_settings.Project.Get(toolName)?.Permission is { } p) return p;
        if (_settings.User.Get(toolName)?.Permission is { } g) return g;

        return _tools.Get(toolName)?.DefaultPermission ?? PermissionLevel.Ask;
    }

    public bool RestrictToWorkspace(string toolName)
    {
        if (_settings.Project.Get(toolName)?.RestrictToWorkspace is { } p) return p;
        if (_settings.User.Get(toolName)?.RestrictToWorkspace is { } g) return g;

        return _tools.Get(toolName)?.SupportsWorkspaceRestriction ?? false;
    }
}
