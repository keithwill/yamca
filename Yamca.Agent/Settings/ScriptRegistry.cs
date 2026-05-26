namespace Yamca.Agent.Settings;

/// <summary>One registered script entry. <see cref="Path"/> is workspace-relative
/// (forward slashes). <see cref="Description"/> is user-authored, surfaced to the
/// LLM at session start.</summary>
public sealed record RegisteredScript(string Path, string? Description);

/// <summary>One registered script directory. Files under this directory (recursive)
/// are treated as registered.</summary>
public sealed record RegisteredScriptDirectory(string Path, string? Description);

/// <summary>User-curated list of scripts the LLM is permitted to run via the
/// <c>execute_registered_script</c> tool. Stored per tier (global + project) in
/// localStorage and merged at use site.</summary>
public sealed class ScriptRegistry
{
    public static ScriptRegistry Empty { get; } = new(
        Array.Empty<RegisteredScript>(),
        Array.Empty<RegisteredScriptDirectory>());

    public IReadOnlyList<RegisteredScript> Registered { get; }
    public IReadOnlyList<RegisteredScriptDirectory> Directories { get; }

    public ScriptRegistry(
        IReadOnlyList<RegisteredScript> registered,
        IReadOnlyList<RegisteredScriptDirectory> directories)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(directories);
        Registered = registered;
        Directories = directories;
    }

    public bool IsEmpty => Registered.Count == 0 && Directories.Count == 0;
}
