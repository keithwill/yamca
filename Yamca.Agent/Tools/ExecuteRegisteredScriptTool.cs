using System.Text;
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

    public string Description => "Run a user-registered script by path (workspace-relative). " +
        "Yamca dispatches to the correct interpreter (PowerShell, sh, Python, Node, tsx/ts-node) " +
        "with no shell in the loop. Use this for build/test/deploy entry points the user has registered. " +
        "Unregistered scripts must go through execute_discovered_script instead.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "script_path":     { "type": "string", "description": "Workspace-relative path to a registered script." },
        "arguments":       { "type": "array", "items": { "type": "string" }, "description": "Arguments passed as argv." },
        "timeout_seconds": { "type": "integer", "description": "Maximum runtime before the script is killed. Default 60.", "minimum": 1, "maximum": 600 }
      },
      "required": ["script_path"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public string? SessionStartMessage(ToolContext context)
    {
        if (_registry.IsEmpty)
        {
            return "The execute_registered_script and execute_discovered_script tools are available, " +
                   "but no scripts are registered for this workspace yet. " +
                   "Users can register scripts via the permissions UI; until then, prefer execute_command " +
                   "or propose registration via execute_discovered_script.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("The execute_registered_script and execute_discovered_script tools are available.");
        sb.AppendLine("Prefer registered scripts for build, test, and deploy operations over execute_command.");
        sb.AppendLine();
        sb.AppendLine("Registered scripts for this project:");
        foreach (var (entry, _) in _registry.AllRegistered())
            sb.Append("  ").Append(entry.Path).Append(string.IsNullOrWhiteSpace(entry.Description) ? "" : "  — " + entry.Description).AppendLine();

        var dirs = _registry.AllDirectories().ToList();
        if (dirs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Registered script directories (any file inside counts as registered):");
            foreach (var (dir, _) in dirs)
                sb.Append("  ").Append(dir.Path).Append(string.IsNullOrWhiteSpace(dir.Description) ? "" : "  — " + dir.Description).AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Other script-shaped files in the workspace are not registered. You may propose running them via");
        sb.AppendLine("execute_discovered_script; the user will be asked to approve each invocation and may choose to register the script.");
        sb.Append("Do not create or modify registered scripts unless asked.");
        return sb.ToString();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var error))
            return ToolResult.Error(error);

        if (!ToolArguments.TryResolvePath(context, scriptPath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        if (!_registry.IsRegistered(resolved, context.Workspace))
        {
            return ToolResult.Error(
                $"Script '{scriptPath}' is not in the registry. Use execute_discovered_script instead; " +
                "the user can choose to register it during approval.");
        }

        return await _runner.RunAsync(resolved, args, timeoutSeconds, context, cancellationToken).ConfigureAwait(false);
    }
}
