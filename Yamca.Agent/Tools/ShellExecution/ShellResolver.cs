using System.Collections.Concurrent;
using System.Diagnostics;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools.ShellExecution;

public enum ShellKind { Pwsh, WindowsPowerShell, Cmd, GitBash, Bash, Sh }

/// <summary>How <see cref="ExecuteCommandTool"/> (and the inline-script path) choose which
/// host shell to drive. <see cref="Auto"/> keeps the historic per-OS auto-detection; every
/// other value forces a specific shell and falls back to <see cref="Auto"/> when that shell
/// is not present on the machine.</summary>
public enum ShellPreference { Auto, Pwsh, WindowsPowerShell, Cmd, GitBash, Bash, Sh }

public sealed record ResolvedShell(string DisplayName, string ExecutablePath, ShellKind Kind);

/// <summary>Outcome of resolving a <see cref="ShellPreference"/>: the shell that will actually
/// run, the preference that was requested, and whether the requested shell was unavailable so
/// we fell back to auto-detection.</summary>
public sealed record ShellResolution(ResolvedShell Shell, ShellPreference Requested, bool FellBack);

/// <summary>
/// Detects which shell <see cref="ExecuteCommandTool"/> should drive on this machine.
/// Resolution is preference-driven: <see cref="ShellPreference.Auto"/> reproduces the historic
/// per-OS detection, while an explicit preference forces a specific shell (falling back to Auto
/// when it is not installed). Results are cached per preference for the process lifetime. Reuses
/// <see cref="InterpreterResolver"/> for the PATH walk so executable discovery has a single
/// source of truth.
/// </summary>
public sealed class ShellResolver
{
    private readonly InterpreterResolver _interpreters;
    private readonly ConcurrentDictionary<ShellPreference, ShellResolution> _cache = new();

    public ShellResolver(InterpreterResolver interpreters)
    {
        ArgumentNullException.ThrowIfNull(interpreters);
        _interpreters = interpreters;
    }

    /// <summary>Resolves the shell that will run for <paramref name="preference"/>, after any
    /// fallback. Use <see cref="ResolveDetailed"/> when you also need to know whether a fallback
    /// happened.</summary>
    public ResolvedShell Resolve(ShellPreference preference = ShellPreference.Auto)
        => ResolveDetailed(preference).Shell;

    /// <summary>Resolves <paramref name="preference"/> and reports whether the requested shell was
    /// unavailable (in which case the result is the auto-detected shell instead).</summary>
    public ShellResolution ResolveDetailed(ShellPreference preference)
        => _cache.GetOrAdd(preference, ResolveCore);

    /// <summary>The specific shells installed on this machine, in display order. Excludes
    /// <see cref="ShellPreference.Auto"/> (always selectable). Used by the settings UI so the
    /// shell dropdown only offers shells that can actually run. A preference is "available" when
    /// it resolves to its own shell rather than falling back to auto-detection.</summary>
    public IReadOnlyList<ShellPreference> AvailablePreferences()
    {
        var ordered = new[]
        {
            ShellPreference.Pwsh,
            ShellPreference.WindowsPowerShell,
            ShellPreference.Cmd,
            ShellPreference.GitBash,
            ShellPreference.Bash,
            ShellPreference.Sh,
        };

        var available = new List<ShellPreference>(ordered.Length);
        foreach (var preference in ordered)
        {
            if (!ResolveDetailed(preference).FellBack)
                available.Add(preference);
        }
        return available;
    }

    private ShellResolution ResolveCore(ShellPreference preference)
    {
        if (preference == ShellPreference.Auto)
            return new ShellResolution(ResolveAuto(), ShellPreference.Auto, FellBack: false);

        var requested = TryResolveKind(PreferenceToKind(preference));
        return requested is not null
            ? new ShellResolution(requested, preference, FellBack: false)
            : new ShellResolution(ResolveAuto(), preference, FellBack: true);
    }

    private ResolvedShell ResolveAuto()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryResolveKind(ShellKind.Pwsh)
                ?? TryResolveKind(ShellKind.WindowsPowerShell)
                ?? CmdShell();
        }

        return TryResolveKind(ShellKind.Bash)
            ?? TryResolveKind(ShellKind.Sh)
            ?? new ResolvedShell("POSIX sh", "/bin/sh", ShellKind.Sh);
    }

    /// <summary>Locates the executable for a single <see cref="ShellKind"/>, or null when that
    /// shell is not available on this machine.</summary>
    private ResolvedShell? TryResolveKind(ShellKind kind)
    {
        switch (kind)
        {
            case ShellKind.Pwsh:
                var pwsh = _interpreters.Resolve(new[] { "pwsh" });
                return pwsh is null ? null : new ResolvedShell("PowerShell 7+ (pwsh)", pwsh, ShellKind.Pwsh);

            case ShellKind.WindowsPowerShell:
                if (!OperatingSystem.IsWindows()) return null;
                var powershell = _interpreters.Resolve(new[] { "powershell" });
                return powershell is null ? null : new ResolvedShell("Windows PowerShell 5.1 (powershell)", powershell, ShellKind.WindowsPowerShell);

            case ShellKind.Cmd:
                return OperatingSystem.IsWindows() ? CmdShell() : null;

            case ShellKind.GitBash:
                var gitBash = ResolveGitBash();
                return gitBash is null ? null : new ResolvedShell("Git Bash", gitBash, ShellKind.GitBash);

            case ShellKind.Bash:
                var bash = _interpreters.Resolve(new[] { "bash" });
                return bash is null ? null : new ResolvedShell("Bash", bash, ShellKind.Bash);

            case ShellKind.Sh:
                var sh = _interpreters.Resolve(new[] { "sh" });
                if (sh is not null) return new ResolvedShell("POSIX sh", sh, ShellKind.Sh);
                // On a POSIX system /bin/sh is a safe assumption; on Windows there is no sh.
                return OperatingSystem.IsWindows() ? null : new ResolvedShell("POSIX sh", "/bin/sh", ShellKind.Sh);

            default:
                return null;
        }
    }

    // cmd.exe is guaranteed on Windows. Prefer %ComSpec% if it points at a real file.
    private static ResolvedShell CmdShell()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        var cmdPath = !string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec)
            ? comspec
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return new ResolvedShell("Windows Command Prompt (cmd.exe)", cmdPath, ShellKind.Cmd);
    }

    /// <summary>Locates the Git for Windows <c>bash.exe</c>, which ships outside PATH. Tries the
    /// standard install locations, then derives the Git root from <c>git</c> on PATH
    /// (…\Git\cmd\git.exe → …\Git\bin\bash.exe). Returns null when Git Bash is not installed or
    /// the OS is not Windows (Git Bash is a Git-for-Windows artifact).</summary>
    private string? ResolveGitBash()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var candidate in GitBashCandidatePaths())
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private IEnumerable<string> GitBashCandidatePaths()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var localAppData = Environment.GetEnvironmentVariable("LocalAppData");

        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "Git", "bin", "bash.exe");
        if (!string.IsNullOrEmpty(programFilesX86))
            yield return Path.Combine(programFilesX86, "Git", "bin", "bash.exe");
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe");

        // Derive from git on PATH: …\Git\cmd\git.exe (or …\Git\bin\git.exe) → …\Git\bin\bash.exe.
        var git = _interpreters.Resolve(new[] { "git" });
        var gitDir = git is null ? null : Path.GetDirectoryName(git);
        var gitRoot = gitDir is null ? null : Path.GetDirectoryName(gitDir);
        if (!string.IsNullOrEmpty(gitRoot))
            yield return Path.Combine(gitRoot, "bin", "bash.exe");
    }

    private static ShellKind PreferenceToKind(ShellPreference preference) => preference switch
    {
        ShellPreference.Pwsh => ShellKind.Pwsh,
        ShellPreference.WindowsPowerShell => ShellKind.WindowsPowerShell,
        ShellPreference.Cmd => ShellKind.Cmd,
        ShellPreference.GitBash => ShellKind.GitBash,
        ShellPreference.Bash => ShellKind.Bash,
        ShellPreference.Sh => ShellKind.Sh,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Auto has no single ShellKind."),
    };

    /// <summary>Builds a <see cref="ProcessStartInfo"/> that runs <paramref name="command"/>
    /// verbatim through the host shell selected by <paramref name="preference"/>. Single source of
    /// truth for how a command line maps to shell arguments per <see cref="ShellKind"/>; shared by
    /// <c>ExecuteCommandTool</c> and the inline-script path.</summary>
    public ProcessStartInfo BuildCommandStartInfo(string command, string workingDirectory, ShellPreference preference = ShellPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(command);
        var shell = Resolve(preference);

        var psi = new ProcessStartInfo
        {
            FileName = shell.ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        switch (shell.Kind)
        {
            case ShellKind.Pwsh:
            case ShellKind.WindowsPowerShell:
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
                break;
            case ShellKind.Cmd:
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
                break;
            case ShellKind.GitBash:
            case ShellKind.Bash:
            case ShellKind.Sh:
            default:
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }

        return psi;
    }
}
