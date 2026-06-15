using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Tools;

/// <summary>
/// Runs an unregistered script by workspace-relative path. Defaults to <see cref="PermissionLevel.Ask"/>
/// so each invocation surfaces an approval prompt, where the user can also choose to promote the
/// script to the registry. A path that is already registered (a registered file or a file under a
/// registered directory) is refused and redirected to <c>execute_allowed</c>, which runs it without
/// asking.
/// </summary>
public sealed class ExecuteScriptTool : ITool
{
    private readonly ScriptRunner _runner;
    private readonly ScriptRegistryLookup _registry;

    public ExecuteScriptTool(ScriptRunner runner, ScriptRegistryLookup registry)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        _runner = runner;
        _registry = registry;
    }

    public string Name => "execute_script";

    public string Description => "Run a script by workspace-relative path. " +
        "Interpreter is resolved automatically (PowerShell, sh, Python, Node, tsx/ts-node). " +
        "Use this for ad-hoc scripts that are not pre-registered; the user is prompted for approval " +
        "and can add the script to the registry. Pre-allowed commands and scripts run via execute_allowed.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "script_path":      { "type": "string", "description": "Workspace-relative path to a script (.ps1, .sh, .py, .js, .mjs, .ts)." },
        "arguments":        { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv." },
        "timeout_seconds":  { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 },
        "max_output_lines": { "type": "integer", "description": "Keep only the last N lines of stdout and stderr. Useful for noisy build/test commands. Default: unrestricted.", "minimum": 1, "maximum": 10000 }
      },
      "required": ["script_path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var maxOutputLines, out var error))
            return ToolResult.Error(error);

        if (!ToolArguments.TryResolvePath(context, scriptPath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (_registry.IsRegistered(resolved, context.Workspace))
        {
            return ToolResult.Error(
                $"Script '{scriptPath}' is registered. Call execute_allowed instead (it runs registered scripts without asking).");
        }

        return await _runner.RunAsync(resolved, args, timeoutSeconds, maxOutputLines, context, cancellationToken).ConfigureAwait(false);
    }
}
