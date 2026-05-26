using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools.ShellExecution;

public enum ShellKind { Pwsh, WindowsPowerShell, Cmd, Bash, Sh }

public sealed record ResolvedShell(string DisplayName, string ExecutablePath, ShellKind Kind);

/// <summary>
/// Detects which shell <see cref="ExecuteCommandTool"/> should drive on this machine
/// and caches the result for the process lifetime. Reuses <see cref="InterpreterResolver"/>
/// for the PATH walk so we have a single source of truth for executable discovery.
/// </summary>
public sealed class ShellResolver
{
    private readonly InterpreterResolver _interpreters;
    private readonly Lock _gate = new();
    private ResolvedShell? _cached;

    public ShellResolver(InterpreterResolver interpreters)
    {
        ArgumentNullException.ThrowIfNull(interpreters);
        _interpreters = interpreters;
    }

    public ResolvedShell Resolve()
    {
        if (_cached is not null) return _cached;
        lock (_gate)
        {
            return _cached ??= ResolveCore();
        }
    }

    private ResolvedShell ResolveCore()
    {
        if (OperatingSystem.IsWindows())
        {
            var pwsh = _interpreters.Resolve(new[] { "pwsh" });
            if (pwsh is not null)
                return new ResolvedShell("PowerShell 7+ (pwsh)", pwsh, ShellKind.Pwsh);

            var powershell = _interpreters.Resolve(new[] { "powershell" });
            if (powershell is not null)
                return new ResolvedShell("Windows PowerShell 5.1 (powershell)", powershell, ShellKind.WindowsPowerShell);

            // cmd.exe is guaranteed on Windows. Prefer %ComSpec% if it points at a real file.
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            var cmdPath = !string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec)
                ? comspec
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            return new ResolvedShell("Windows Command Prompt (cmd.exe)", cmdPath, ShellKind.Cmd);
        }

        var bash = _interpreters.Resolve(new[] { "bash" });
        if (bash is not null)
            return new ResolvedShell("Bash", bash, ShellKind.Bash);

        return new ResolvedShell("POSIX sh", "/bin/sh", ShellKind.Sh);
    }
}
