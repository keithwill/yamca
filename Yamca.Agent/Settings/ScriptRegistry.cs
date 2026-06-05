namespace Yamca.Agent.Settings;

/// <summary>One registered script entry. <see cref="Path"/> is workspace-relative
/// (forward slashes). <see cref="Description"/> is user-authored, surfaced to the
/// LLM at session start. When <see cref="SuppressOutputOnSuccess"/> is set, a
/// successful (exit code 0) run returns only the status to the LLM, not stdout/stderr,
/// to save context.</summary>
public sealed record RegisteredScript(string Path, string? Description, bool SuppressOutputOnSuccess = false);

/// <summary>One registered script directory. Files under this directory (recursive)
/// are treated as registered. <see cref="SuppressOutputOnSuccess"/> applies to every
/// file run via this directory entry.</summary>
public sealed record RegisteredScriptDirectory(string Path, string? Description, bool SuppressOutputOnSuccess = false);

/// <summary>One registered inline script: a literal command line (e.g. <c>npm install</c>)
/// that has no backing file in the workspace. <see cref="Command"/> is run verbatim
/// through the host shell. <see cref="Description"/> is surfaced to the LLM at session
/// start. When <see cref="SuppressOutputOnSuccess"/> is set, a successful run returns
/// only the status to the LLM.</summary>
public sealed record RegisteredInlineScript(string Command, string? Description, bool SuppressOutputOnSuccess = false);

/// <summary>User-curated list of scripts the LLM is permitted to run via the
/// <c>execute_registered_script</c> tool. Stored per tier (user + project) on
/// disk and merged at use site.</summary>
public sealed class ScriptRegistry
{
    public static ScriptRegistry Empty { get; } = new(
        Array.Empty<RegisteredScript>(),
        Array.Empty<RegisteredScriptDirectory>(),
        Array.Empty<RegisteredInlineScript>());

    public IReadOnlyList<RegisteredScript> Registered { get; }
    public IReadOnlyList<RegisteredScriptDirectory> Directories { get; }
    public IReadOnlyList<RegisteredInlineScript> Inline { get; }

    public ScriptRegistry(
        IReadOnlyList<RegisteredScript> registered,
        IReadOnlyList<RegisteredScriptDirectory> directories,
        IReadOnlyList<RegisteredInlineScript> inline)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(inline);
        Registered = registered;
        Directories = directories;
        Inline = inline;
    }

    public bool IsEmpty => Registered.Count == 0 && Directories.Count == 0 && Inline.Count == 0;
}
