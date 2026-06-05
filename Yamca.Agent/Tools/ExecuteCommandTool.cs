using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tools;

public sealed class ExecuteCommandTool : ITool
{
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

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "command", out var command, out var argError))
            return ToolResult.Error(argError);

        var timeoutSeconds = 60;
        if (arguments.TryGetProperty("timeout_seconds", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            timeoutSeconds = Math.Clamp(tProp.GetInt32(), 1, 600);

        var psi = _shells.BuildCommandStartInfo(command, context.Workspace.RootPath);
        return await ProcessRunner.RunAsync(psi, timeoutSeconds, maxOutputLines: null, "Command", cancellationToken)
            .ConfigureAwait(false);
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
}
