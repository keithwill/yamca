using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Permissions;
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
    // Permission services are resolved lazily from the scope to break a DI cycle:
    // PermissionResolver depends on IToolRegistry, and IToolRegistry's factory
    // enumerates ITool services — so taking IPermissionResolver here directly
    // would close the loop and stall scope construction.
    private readonly IServiceProvider _services;

    public ExecuteScriptTool(
        ScriptRunner runner,
        ScriptRegistryLookup registry,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        _runner = runner;
        _registry = registry;
        _services = services;
    }

    public string Name => "execute_script";

    public string Description =>
        "Run a script by workspace-relative path. Interpreter is resolved automatically " +
        "(PowerShell, sh, Python, Node, tsx/ts-node).";

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

        return sb.ToString();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ScriptToolArgs.TryParse(arguments, out var scriptPath, out var args, out var timeoutSeconds, out var error))
            return ToolResult.Error(error);

        if (!ToolArguments.TryResolvePath(context, scriptPath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        var isRegistered = _registry.IsRegistered(resolved, context.Workspace);
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

        return await _runner.RunAsync(resolved, args, timeoutSeconds, context, cancellationToken).ConfigureAwait(false);
    }
}
