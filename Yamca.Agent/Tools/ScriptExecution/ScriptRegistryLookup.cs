using Yamca.Agent.Settings;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Tools.ScriptExecution;

/// <summary>
/// Decides whether a resolved script path falls under the user's registered scripts
/// or registered script directories (union of project + user tiers). Path comparisons
/// honor the host OS case-sensitivity rules.
/// </summary>
public sealed class ScriptRegistryLookup
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly ISessionSettings _settings;

    public ScriptRegistryLookup(ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public bool IsRegistered(string resolvedScriptPath, IWorkspace workspace)
    {
        ArgumentException.ThrowIfNullOrEmpty(resolvedScriptPath);
        ArgumentNullException.ThrowIfNull(workspace);

        foreach (var tier in new[] { _settings.ProjectScripts, _settings.UserScripts })
        {
            foreach (var entry in tier.Registered)
            {
                if (TryResolveWithinWorkspace(workspace, entry.Path, out var entryResolved)
                    && string.Equals(entryResolved, resolvedScriptPath, PathComparison))
                {
                    return true;
                }
            }

            foreach (var dir in tier.Directories)
            {
                if (TryResolveWithinWorkspace(workspace, dir.Path, out var dirResolved)
                    && IsUnder(resolvedScriptPath, dirResolved))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsEmpty =>
        _settings.ProjectScripts.IsEmpty && _settings.UserScripts.IsEmpty;

    public IEnumerable<(RegisteredScript Entry, SettingsTierTag Tier)> AllRegistered()
    {
        foreach (var e in _settings.ProjectScripts.Registered) yield return (e, SettingsTierTag.Project);
        foreach (var e in _settings.UserScripts.Registered)  yield return (e, SettingsTierTag.User);
    }

    public IEnumerable<(RegisteredScriptDirectory Entry, SettingsTierTag Tier)> AllDirectories()
    {
        foreach (var d in _settings.ProjectScripts.Directories) yield return (d, SettingsTierTag.Project);
        foreach (var d in _settings.UserScripts.Directories)  yield return (d, SettingsTierTag.User);
    }

    private static bool TryResolveWithinWorkspace(IWorkspace ws, string requested, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(requested)) return false;
        try
        {
            resolved = ws.Resolve(requested);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnder(string fullPath, string directory)
    {
        if (string.Equals(fullPath, directory, PathComparison)) return false;
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison);
    }
}

public enum SettingsTierTag { Project, User }
