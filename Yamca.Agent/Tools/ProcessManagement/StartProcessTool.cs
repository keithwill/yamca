using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>LLM-facing facade for starting a long-lived background process that keeps running after
/// the chat turn (and session) ends. Like <c>execute_script</c> / <c>git</c>, it passes the
/// AgentLoop permission gate (<see cref="DefaultPermission"/> = Allow) and runs the real check
/// internally under one of two identities so each is independently configurable:
/// <list type="bullet">
/// <item><c>execute_registered_script</c> when the target names a registered inline command — the
/// same green-light that governs running that command one-shot, so allowing it there also allows
/// backgrounding it.</item>
/// <item><c>start_process_command</c> (default Ask) for an arbitrary command line.</item>
/// </list>
/// Deferred so its schema stays out of the prompt prefix.</summary>
public sealed class StartProcessTool : ITool
{
    private readonly IBackgroundProcessManager _manager;
    private readonly ISessionSettings _settings;
    private readonly ScriptRegistryLookup _registry;
    // Permission services are resolved lazily from the scope to break a DI cycle — see the
    // matching note on ExecuteScriptTool.
    private readonly IServiceProvider _services;

    public StartProcessTool(
        IBackgroundProcessManager manager,
        ISessionSettings settings,
        ScriptRegistryLookup registry,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        _manager = manager;
        _settings = settings;
        _registry = registry;
        _services = services;
    }

    public string Name => "start_process";

    public string Description =>
        "Start a long-lived background process (e.g. a dev server or watcher) and return immediately. " +
        "The process keeps running after this chat session ends; manage it with get_process_output, " +
        "list_processes, and stop_process. Reuses an existing process of the same name if one is already running. " +
        "To launch a registered command, pass its name (or exact command line) as 'command'; it then runs " +
        "under the registered-script permission instead of asking.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "name":              { "type": "string", "description": "Stable, human-readable id for the process (e.g. \"web\"). Optional when launching a registered command that has a name — that name is used. Used by the other process tools." },
        "command":           { "type": "string", "description": "The shell command line to run, or the name (or exact command line) of a registered command to launch in the background." },
        "working_directory": { "type": "string", "description": "Directory to run in. Defaults to the workspace root." },
        "stop_command":      { "type": "string", "description": "Optional command run to shut the process down gracefully before it is force-killed." },
        "ports":             { "type": "array", "items": { "type": "integer" }, "description": "Ports this process listens on, surfaced in list_processes to spot conflicts." }
      },
      "required": ["command"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    // Allow so the AgentLoop passes through; the real check runs internally under
    // execute_registered_script (registered command) or start_process_command (arbitrary).
    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedInSettings => false;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "command", out var command, out var commandError))
            return ToolResult.Error(commandError);
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("Argument 'command' must not be empty.");

        string? name = null;
        if (arguments.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            name = nameProp.GetString();

        var workingDirectory = context.Workspace.RootPath;
        if (arguments.TryGetProperty("working_directory", out var wdProp) && wdProp.ValueKind == JsonValueKind.String)
        {
            if (!ToolArguments.TryResolvePath(context, wdProp.GetString() ?? string.Empty, out workingDirectory, out var wdError))
                return ToolResult.Error(wdError);
        }

        string? stopCommand = null;
        if (arguments.TryGetProperty("stop_command", out var scProp) && scProp.ValueKind == JsonValueKind.String)
            stopCommand = scProp.GetString();

        if (!TryGetPorts(arguments, out var ports, out var portsError))
            return ToolResult.Error(portsError);

        // A registered inline command (matched by name or verbatim) rides the registered-script
        // permission; anything else is an arbitrary background command gated by start_process_command.
        var registered = _registry.TryResolveInline(command, out var inlineEntry);
        var effectiveName = registered ? "execute_registered_script" : "start_process_command";

        var permissions = _services.GetRequiredService<IPermissionResolver>();
        var level = permissions.Resolve(effectiveName);
        if (level == PermissionLevel.Ask)
        {
            var approvals = _services.GetRequiredService<IApprovalCoordinator>();
            var decision = await approvals.RequestApprovalAsync(effectiveName, arguments, cancellationToken).ConfigureAwait(false);
            level = decision.Approved ? PermissionLevel.Allow : PermissionLevel.Deny;
            // Persist approvals only; a rejection is one-shot (no stored Deny — use Hidden instead).
            if (decision.Approved && decision.Persistence != ApprovalPersistence.None)
                _services.GetRequiredService<IPermissionStore>().Persist(effectiveName, level, decision.Persistence);
        }
        if (level == PermissionLevel.Deny)
            return ToolResult.Error($"Permission denied for '{effectiveName}'.");

        // For a registered command, launch its stored command line; the supplied token may have been a name.
        var commandLine = registered ? inlineEntry.Command : command;

        // Process name: an explicit name wins; otherwise fall back to a registered command's name.
        var processName = !string.IsNullOrWhiteSpace(name)
            ? name!.Trim()
            : (registered ? inlineEntry.Name?.Trim() ?? string.Empty : string.Empty);
        if (string.IsNullOrWhiteSpace(processName))
            return ToolResult.Error("Argument 'name' is required (the registered command has no name to fall back to).");

        var request = new StartRequest(processName, commandLine, workingDirectory, stopCommand, ports, _settings.ShellPreference);
        return BackgroundProcessLauncher.Start(_manager, request);
    }

    /// <summary>Parse the optional integer <c>ports</c> array.</summary>
    private static bool TryGetPorts(JsonElement args, out IReadOnlyList<int> ports, out string error)
    {
        ports = Array.Empty<int>();
        error = string.Empty;
        if (!args.TryGetProperty("ports", out var prop) || prop.ValueKind == JsonValueKind.Null)
            return true;
        if (prop.ValueKind != JsonValueKind.Array)
        {
            error = "Argument 'ports' must be an array of integers.";
            return false;
        }

        var list = new List<int>(prop.GetArrayLength());
        foreach (var element in prop.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var port))
            {
                error = "Every entry in 'ports' must be an integer.";
                return false;
            }
            list.Add(port);
        }
        ports = list;
        return true;
    }
}
