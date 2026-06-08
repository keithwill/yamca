using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools;

/// <summary>
/// LLM-facing facade over the two underlying script tools. The LLM calls this single
/// tool; internally it checks registration status and dispatches the permission check
/// under "execute_registered_script" or "execute_discovered_script" so the settings
/// UI permissions for each type remain independently configurable.
/// </summary>
public sealed class ExecuteScriptTool : ITool
{
    private readonly ScriptRunner _runner;
    private readonly ScriptRegistryLookup _registry;
    private readonly ISessionSettings _settings;
    private readonly IBackgroundProcessManager _processes;
    // Permission services are resolved lazily from the scope to break a DI cycle:
    // PermissionResolver depends on IToolRegistry, and IToolRegistry's factory
    // enumerates ITool services — so taking IPermissionResolver here directly
    // would close the loop and stall scope construction.
    private readonly IServiceProvider _services;

    public ExecuteScriptTool(
        ScriptRunner runner,
        ScriptRegistryLookup registry,
        ISessionSettings settings,
        IBackgroundProcessManager processes,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(services);
        _runner = runner;
        _registry = registry;
        _settings = settings;
        _processes = processes;
        _services = services;
    }

    public string Name => "execute_script";

    public string Description =>
        "Run a script by workspace-relative path. Interpreter is resolved automatically " +
        "(PowerShell, sh, Python, Node, tsx/ts-node). To run a registered inline script " +
        "(a bare command listed at session start, e.g. 'npm install'), pass its name or its " +
        "exact command line as script_path; inline scripts ignore 'arguments'. Inline commands " +
        "marked [background] (e.g. a watcher or dev server) are launched as a long-lived process " +
        "instead of run to completion; manage them with get_process_output, list_processes, and stop_process.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "script_path":      { "type": "string", "description": "Workspace-relative path to a script (.ps1, .sh, .py, .js, .mjs, .ts), or the name or exact command line of a registered inline script." },
        "arguments":        { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv. Ignored for inline scripts." },
        "timeout_seconds":  { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 },
        "max_output_lines": { "type": "integer", "description": "Keep only the last N lines of stdout and stderr. Useful for noisy build/test commands. Default: unrestricted.", "minimum": 1, "maximum": 10000 }
      },
      "required": ["script_path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    // Allow so the AgentLoop passes through; real permission checks are done internally
    // under the effective tool name (execute_registered_script or execute_discovered_script).
    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedInSettings => false;

    public bool Deferred => true;

    public string? SessionStartMessage(ToolContext context)
    {
        if (_registry.IsEmpty)
            return "";

        var sb = new StringBuilder();

        if (_registry.AllRegistered().Count() > 0)
        {
            sb.AppendLine("Registered Scripts:");
            foreach (var (entry, _) in _registry.AllRegistered())
                sb.Append("  ").Append(entry.Path)
                .Append(string.IsNullOrWhiteSpace(entry.Description) ? "" : "  — " + entry.Description)
                .AppendLine();
        }

        var dirs = _registry.AllDirectories().ToList();
        if (dirs.Count > 0)
        {
            sb.AppendLine("Registered Script Directories:");
            foreach (var (dir, _) in dirs)
                sb.Append("  ").Append(dir.Path)
                  .Append(string.IsNullOrWhiteSpace(dir.Description) ? "" : "  — " + dir.Description)
                  .AppendLine();
        }

        var inline = _registry.AllInline().ToList();
        if (inline.Count > 0)
        {
            sb.AppendLine("Registered Inline Scripts (pass the name or the command verbatim as script_path):");
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
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var maxOutputLines, out var error))
            return ToolResult.Error(error);

        // Inline scripts have no backing file: match the name or literal command line against
        // the registry first. A match always runs under the registered-script permission.
        var isInline = _registry.TryResolveInline(scriptPath, out var inlineEntry);

        string resolved = string.Empty;
        if (!isInline && !ToolArguments.TryResolvePath(context, scriptPath, out resolved, out var pathError))
            return ToolResult.Error(pathError);

        var suppressOutputOnSuccess = isInline && inlineEntry.SuppressOutputOnSuccess;
        var isRegistered = isInline
            || _registry.IsRegistered(resolved, context.Workspace, out suppressOutputOnSuccess);
        var effectiveName = isRegistered ? "execute_registered_script" : "execute_discovered_script";

        var permissions = _services.GetRequiredService<IPermissionResolver>();
        var level = permissions.Resolve(effectiveName);
        if (level == PermissionLevel.Ask)
        {
            var approvals = _services.GetRequiredService<IApprovalCoordinator>();
            var decision = await approvals.RequestApprovalAsync(effectiveName, arguments, cancellationToken).ConfigureAwait(false);
            level = decision.Approved ? PermissionLevel.Allow : PermissionLevel.Deny;
            if (decision.Persistence != ApprovalPersistence.None)
                _services.GetRequiredService<IPermissionStore>().Persist(effectiveName, level, decision.Persistence);
        }

        if (level == PermissionLevel.Deny)
            return ToolResult.Error($"Permission denied for '{effectiveName}'.");

        // A background-flagged inline command is long-lived: hand it to the process manager rather
        // than running it to completion, so "run watch" launches a managed process without the model
        // needing to reach for start_process. The process name is the command's name, else the command.
        if (isInline && inlineEntry.Background)
        {
            var processName = string.IsNullOrWhiteSpace(inlineEntry.Name) ? inlineEntry.Command : inlineEntry.Name!.Trim();
            var request = new StartRequest(processName, inlineEntry.Command, context.Workspace.RootPath, StopCommand: null, Array.Empty<int>(), _settings.ShellPreference);
            return BackgroundProcessLauncher.Start(_processes, request);
        }

        return isInline
            ? await _runner.RunInlineAsync(inlineEntry.Command, timeoutSeconds, maxOutputLines, context, cancellationToken, suppressOutputOnSuccess, _settings.ShellPreference).ConfigureAwait(false)
            : await _runner.RunAsync(resolved, args, timeoutSeconds, maxOutputLines, context, cancellationToken, suppressOutputOnSuccess).ConfigureAwait(false);
    }
}
