using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Starts a long-lived background process that keeps running after the chat turn (and
/// session) ends. Deferred so its schema stays out of the prompt prefix.</summary>
public sealed class StartProcessTool : ITool
{
    private readonly IBackgroundProcessManager _manager;
    private readonly ISessionSettings _settings;

    public StartProcessTool(IBackgroundProcessManager manager, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        _manager = manager;
        _settings = settings;
    }

    public string Name => "start_process";

    public string Description =>
        "Start a long-lived background process (e.g. a dev server or watcher) and return immediately. " +
        "The process keeps running after this chat session ends; manage it with get_process_output, " +
        "list_processes, and stop_process. Reuses an existing process of the same name if one is already running.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "name":              { "type": "string", "description": "Stable, human-readable id for the process (e.g. \"web\"). Used by the other process tools." },
        "command":           { "type": "string", "description": "The shell command line to run. Uses the session's configured shell." },
        "working_directory": { "type": "string", "description": "Directory to run in. Defaults to the workspace root." },
        "stop_command":      { "type": "string", "description": "Optional command run to shut the process down gracefully before it is force-killed." },
        "ports":             { "type": "array", "items": { "type": "integer" }, "description": "Ports this process listens on, surfaced in list_processes to spot conflicts." }
      },
      "required": ["name", "command"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var nameError))
            return Task.FromResult(ToolResult.Error(nameError));
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Error("Argument 'name' must not be empty."));
        if (!ToolArguments.TryGetString(arguments, "command", out var command, out var commandError))
            return Task.FromResult(ToolResult.Error(commandError));

        var workingDirectory = context.Workspace.RootPath;
        if (arguments.TryGetProperty("working_directory", out var wdProp) && wdProp.ValueKind == JsonValueKind.String)
        {
            if (!ToolArguments.TryResolvePath(context, wdProp.GetString() ?? string.Empty, out workingDirectory, out var wdError))
                return Task.FromResult(ToolResult.Error(wdError));
        }

        string? stopCommand = null;
        if (arguments.TryGetProperty("stop_command", out var scProp) && scProp.ValueKind == JsonValueKind.String)
            stopCommand = scProp.GetString();

        if (!TryGetPorts(arguments, out var ports, out var portsError))
            return Task.FromResult(ToolResult.Error(portsError));

        var request = new StartRequest(name, command, workingDirectory, stopCommand, ports, _settings.ShellPreference);
        var outcome = _manager.Start(request);
        var p = outcome.Process;

        var sb = new StringBuilder();
        if (outcome.AlreadyRunning)
            sb.Append("A process named '").Append(name).Append("' is already running (pid ").Append(p.Pid).Append("); reused it.\n");
        else if (p.Status == ProcessStatus.Failed)
            return Task.FromResult(ToolResult.Error($"Failed to start '{name}':\n{p.RenderTail()}"));
        else
            sb.Append("Started '").Append(name).Append("' (pid ").Append(p.Pid).Append(").\n");

        sb.Append("status: ").Append(p.Status.ToString().ToLowerInvariant());
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
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
