using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools;

/// <summary>
/// Runs a script that is NOT in the registry. Defaults to <see cref="PermissionLevel.Ask"/>
/// so each invocation surfaces an approval prompt, where the user can also choose to
/// promote the script to the registry.
/// </summary>
public sealed class ExecuteDiscoveredScriptTool : ITool
{
    private readonly ScriptRunner _runner;
    private readonly ScriptRegistryLookup _registry;

    public ExecuteDiscoveredScriptTool(ScriptRunner runner, ScriptRegistryLookup registry)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        _runner = runner;
        _registry = registry;
    }

    public string Name => "execute_discovered_script";

    public string Description => "Propose running an unregistered script. " +
        "Prompts the user for approval and offers to add it to the registry. " +
        "Once registered, use execute_registered_script instead.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "script_path":     { "type": "string", "description": "Workspace-relative path to a script (.ps1, .sh, .py, .js, .mjs, .ts)." },
        "arguments":       { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv." },
        "timeout_seconds": { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 }
      },
      "required": ["script_path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool ExposedToLlm => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var error))
            return ToolResult.Error(error);

        if (!ToolArguments.TryResolvePath(context, scriptPath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (_registry.IsRegistered(resolved, context.Workspace))
        {
            return ToolResult.Error(
                $"Script '{scriptPath}' is already registered. Call execute_registered_script instead.");
        }

        return await _runner.RunAsync(resolved, args, timeoutSeconds, context, cancellationToken).ConfigureAwait(false);
    }
}
