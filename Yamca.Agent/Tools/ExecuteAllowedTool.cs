using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools;

/// <summary>
/// Runs an allowlisted entry — a registered command (by name) or a registered script (by
/// workspace-relative path, including a file under a registered directory). This is the curated,
/// always-Allow execution path: its permission is fixed at <see cref="PermissionLevel.Allow"/> and
/// not user-configurable, since green-lighting the registry is its entire purpose. Ad-hoc,
/// unregistered scripts go through <c>execute_script</c> (default Ask) instead.
/// </summary>
public sealed class ExecuteAllowedTool : ITool
{
    private readonly ScriptRunner _runner;
    private readonly ScriptRegistryLookup _registry;
    private readonly ISessionSettings _settings;
    private readonly IBackgroundProcessManager _processes;

    public ExecuteAllowedTool(
        ScriptRunner runner,
        ScriptRegistryLookup registry,
        ISessionSettings settings,
        IBackgroundProcessManager processes)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(processes);
        _runner = runner;
        _registry = registry;
        _settings = settings;
        _processes = processes;
    }

    public string Name => "execute_allowed";

    public string Description =>
        "Run a pre-allowed command or script — the curated entry points the user has registered. " +
        "Pass a registered command's name (a bare command listed at session start, e.g. 'install'), " +
        "or the workspace-relative path to a registered script (interpreter resolved automatically: " +
        "PowerShell, sh, Python, Node, tsx/ts-node). A registered command marked [background] " +
        "(a watcher or dev server) is launched as a long-lived process instead of run to completion; " +
        "manage it with get_process_output, list_processes, and stop_process. Prefer this over " +
        "execute_command / execute_script for anything listed at session start. Note: on a successful " +
        "run the command/script output may be withheld to save context, leaving only the exit status.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "command_or_script": { "type": "string", "description": "A registered command's name, or the workspace-relative path to a registered script (or a file under a registered directory)." },
        "arguments":         { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv. Ignored for registered commands (which run verbatim)." },
        "timeout_seconds":   { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 },
        "max_output_lines":  { "type": "integer", "description": "Keep only the last N lines of stdout and stderr. Useful for noisy build/test commands. Default: unrestricted.", "minimum": 1, "maximum": 10000 }
      },
      "required": ["command_or_script"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    // Always Allow — the registry IS the allowlist, so there is nothing for the user to gate here.
    public bool ConfigurablePermission => false;

    public bool Deferred => true;

    public string? SessionStartMessage(ToolContext context)
    {
        if (_registry.IsEmpty)
            return "";

        var sb = new StringBuilder();

        if (_registry.AllRegistered().Any())
        {
            sb.AppendLine("Registered Scripts (run via execute_allowed by passing the path as command_or_script):");
            foreach (var (entry, _) in _registry.AllRegistered())
                sb.Append("  ").Append(entry.Path)
                .Append(string.IsNullOrWhiteSpace(entry.Description) ? "" : "  — " + entry.Description)
                .AppendLine();
        }

        var dirs = _registry.AllDirectories().ToList();
        if (dirs.Count > 0)
        {
            sb.AppendLine("Registered Script Directories (any file under these is runnable via execute_allowed by path):");
            foreach (var (dir, _) in dirs)
                sb.Append("  ").Append(dir.Path)
                  .Append(string.IsNullOrWhiteSpace(dir.Description) ? "" : "  — " + dir.Description)
                  .AppendLine();
        }

        var inline = _registry.AllInline().ToList();
        if (inline.Count > 0)
        {
            sb.AppendLine("Registered Commands (run via execute_allowed by passing the name as command_or_script):");
            foreach (var (cmd, _) in inline)
                sb.Append("  ")
                  .Append(string.IsNullOrWhiteSpace(cmd.Name) ? cmd.Command : cmd.Name + "  (" + cmd.Command + ")")
                  .Append(cmd.Background ? "  [background]" : "")
                  .Append(string.IsNullOrWhiteSpace(cmd.Description) ? "" : "  — " + cmd.Description)
                  .AppendLine();
        }

        return sb.ToString();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var target, out var args, out var timeoutSeconds, out var maxOutputLines, out var error, targetProperty: "command_or_script"))
            return ToolResult.Error(error);

        // Registered commands are matched first (by name or verbatim command line), so a command
        // name wins over a script path that happens to be spelled the same.
        if (_registry.TryResolveInline(target, out var inlineEntry))
        {
            // A background-flagged command is long-lived: hand it to the process manager rather than
            // running it to completion, so "run watch" launches a managed process without the model
            // needing to reach for start_process. The process name is the command's name, else the command.
            if (inlineEntry.Background)
            {
                var processName = string.IsNullOrWhiteSpace(inlineEntry.Name) ? inlineEntry.Command : inlineEntry.Name!.Trim();
                var request = new StartRequest(processName, inlineEntry.Command, context.Workspace.RootPath, StopCommand: null, Array.Empty<int>(), _settings.ShellPreference);
                return BackgroundProcessLauncher.Start(_processes, request);
            }

            return await _runner.RunInlineAsync(inlineEntry.Command, timeoutSeconds, maxOutputLines, context, cancellationToken, inlineEntry.SuppressOutputOnSuccess, _settings.ShellPreference).ConfigureAwait(false);
        }

        // Otherwise treat the target as a workspace-relative script path; it must be registered
        // (a registered file or a file under a registered directory).
        if (!ToolArguments.TryResolvePath(context, target, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!_registry.IsRegistered(resolved, context.Workspace, out var suppressOutputOnSuccess))
        {
            return ToolResult.Error(
                $"'{target}' is not a registered command or script. Use execute_script to run an " +
                "unregistered script by path; the user can choose to register it during approval.");
        }

        return await _runner.RunAsync(resolved, args, timeoutSeconds, maxOutputLines, context, cancellationToken, suppressOutputOnSuccess).ConfigureAwait(false);
    }
}
