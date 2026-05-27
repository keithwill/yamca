using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tools;

public sealed class ExecuteCommandTool : ITool
{
    // Cap captured output so a runaway command doesn't blow up the chat context.
    private const int MaxStreamChars = 16_000;

    private readonly ShellResolver _shells;

    public ExecuteCommandTool(ShellResolver shells)
    {
        ArgumentNullException.ThrowIfNull(shells);
        _shells = shells;
    }

    public string Name => "execute_command";

    public string Description => "Run a shell command; returns stdout, stderr, and exit code. Shell type is noted at session start.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "command":          { "type": "string", "description": "The shell command line to execute." },
        "timeout_seconds":  { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 }
      },
      "required": ["command"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "command", out var command, out var argError))
            return ToolResult.Error(argError);

        var timeoutSeconds = 60;
        if (arguments.TryGetProperty("timeout_seconds", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            timeoutSeconds = Math.Clamp(tProp.GetInt32(), 1, 600);

        var psi = BuildStartInfo(command, context.Workspace.RootPath, _shells.Resolve());

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived  += (_, e) => AppendCapped(stderr, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start command: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var reason = timeoutCts.IsCancellationRequested ? $"timed out after {timeoutSeconds}s" : "cancelled";
            return ToolResult.Error($"Command {reason}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        // Flush async output handlers.
        process.WaitForExit();

        var summary = new StringBuilder();
        summary.Append("exit_code: ").Append(process.ExitCode).Append('\n');
        summary.Append("stdout:\n").Append(stdout.ToString());
        if (stdout.Length == 0 || stdout[^1] != '\n') summary.Append('\n');
        summary.Append("stderr:\n").Append(stderr.ToString());

        return process.ExitCode == 0
            ? ToolResult.Ok(summary.ToString())
            : ToolResult.Error(summary.ToString());
    }

    private static ProcessStartInfo BuildStartInfo(string command, string workingDirectory, ResolvedShell shell)
    {
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
            case ShellKind.Bash:
            case ShellKind.Sh:
            default:
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }

        return psi;
    }

    public string? SessionStartMessage(ToolContext context)
    {
        var shell = _shells.Resolve();
        return shell.Kind switch
        {
            ShellKind.Pwsh =>
                $"The execute_command tool runs commands via PowerShell 7+ (pwsh) at `{shell.ExecutablePath}` " +
                "with `-NoLogo -NoProfile -NonInteractive -Command`. Use PowerShell syntax.",
            ShellKind.WindowsPowerShell =>
                $"The execute_command tool runs commands via Windows PowerShell 5.1 at `{shell.ExecutablePath}` " +
                "with `-NoLogo -NoProfile -NonInteractive -Command`. Use Windows PowerShell syntax (pwsh 7+ is not installed on this machine).",
            ShellKind.Cmd =>
                $"The execute_command tool runs commands via the Windows Command Prompt (cmd.exe) at `{shell.ExecutablePath}` " +
                "with `/c`. Use cmd.exe syntax — PowerShell is not available on this machine.",
            ShellKind.Bash =>
                $"The execute_command tool runs commands via Bash at `{shell.ExecutablePath}` with `-c`. Use Bash syntax.",
            ShellKind.Sh =>
                $"The execute_command tool runs commands via POSIX sh at `{shell.ExecutablePath}` with `-c`. Use POSIX shell syntax (bash extensions are not available).",
            _ => null,
        };
    }

    private static void AppendCapped(StringBuilder sink, string? line)
    {
        if (line is null) return;
        var remaining = MaxStreamChars - sink.Length;
        if (remaining <= 0) return;

        if (line.Length + 1 <= remaining)
        {
            sink.Append(line).Append('\n');
        }
        else
        {
            sink.Append(line, 0, Math.Min(line.Length, remaining));
            sink.Append("\n…[truncated]\n");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}
