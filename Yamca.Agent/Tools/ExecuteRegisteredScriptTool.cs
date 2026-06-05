using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools;

/// <summary>
/// Runs a user-registered script. Refuses unregistered paths so the registered tier's
/// <see cref="DefaultPermission"/> = <see cref="PermissionLevel.Allow"/> only ever
/// applies to curated entry points.
/// </summary>
public sealed class ExecuteRegisteredScriptTool : ITool
{
    private readonly ScriptRunner _runner;
    private readonly ScriptRegistryLookup _registry;

    public ExecuteRegisteredScriptTool(ScriptRunner runner, ScriptRegistryLookup registry)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        _runner = runner;
        _registry = registry;
    }

    public string Name => "execute_registered_script";

    public string Description => "Run a pre-registered script by workspace-relative path. " +
        "Interpreter is resolved automatically (PowerShell, sh, Python, Node, tsx/ts-node). " +
        "Unregistered scripts must use execute_discovered_script.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "script_path":     { "type": "string", "description": "Workspace-relative path to a registered script." },
        "arguments":       { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv." },
        "timeout_seconds": { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 }
      },
      "required": ["script_path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public bool ExposedToLlm => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var maxOutputLines, out var error))
            return ToolResult.Error(error);

        if (!ToolArguments.TryResolvePath(context, scriptPath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!_registry.IsRegistered(resolved, context.Workspace, out var suppressOutputOnSuccess))
        {
            return ToolResult.Error(
                $"Script '{scriptPath}' is not in the registry. Use execute_discovered_script instead; " +
                "the user can choose to register it during approval.");
        }

        return await _runner.RunAsync(resolved, args, timeoutSeconds, maxOutputLines, context, cancellationToken, suppressOutputOnSuccess).ConfigureAwait(false);
    }
}
