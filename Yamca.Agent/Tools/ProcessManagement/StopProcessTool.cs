using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Stops a background process: runs its stop_command (if any), waits a grace period, then
/// force-kills the process tree.</summary>
public sealed class StopProcessTool : ITool
{
    private readonly IBackgroundProcessManager _manager;

    public StopProcessTool(IBackgroundProcessManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public string Name => "stop_process";

    public string Description =>
        "Stop a background process started with start_process. Runs its stop_command (if set), " +
        "waits briefly, then force-kills the process tree.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "name": { "type": "string", "description": "The process name passed to start_process." }
      },
      "required": ["name"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var nameError))
            return ToolResult.Error(nameError);

        var existed = await _manager.Stop(name).ConfigureAwait(false);
        if (!existed)
            return ToolResult.Error($"No background process named '{name}'.");

        var process = _manager.Get(name);
        var status = process?.Status.ToString().ToLowerInvariant() ?? "stopped";
        return ToolResult.Ok($"Stopped '{name}'. status: {status}");
    }
}
