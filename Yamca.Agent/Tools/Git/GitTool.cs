using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Git;

/// <summary>
/// The single LLM-facing git tool. The model picks a curated <c>operation</c> and passes
/// <c>arguments</c> verbatim as argv; this tool classifies the operation as read or write and
/// runs the real permission check under the <c>git_read</c> / <c>git_write</c> identities (which
/// is where the user configures Allow/Ask). Exposing one tool — not one per subcommand — keeps
/// the model's tool list small; the read/write granularity lives entirely on the permission side.
/// </summary>
public sealed class GitTool : ITool
{
    // Permission services are resolved lazily from the scope to break a DI cycle:
    // IPermissionResolver depends on IToolRegistry, whose factory enumerates ITool services —
    // taking IPermissionResolver in the constructor would close that loop. (Same approach as
    // ExecuteScriptTool.)
    private readonly IServiceProvider _services;

    public GitTool(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public string Name => "git";

    public string Description =>
        "Run a common git subcommand. 'operation' must be one of the read set (" + GitSubcommands.ReadList +
        ") or the write set (" + GitSubcommands.WriteList + "). 'arguments' are passed to git verbatim as " +
        "argv (no shell, so ;, &&, | and $() are inert). For any subcommand not listed here, use execute_command.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "operation":       { "type": "string", "description": "The git subcommand, e.g. 'status', 'log', 'commit'." },
        "arguments":       { "type": "array", "items": { "type": "string" }, "description": "Arguments passed to git as argv, after the subcommand." },
        "timeout_seconds": { "type": "integer", "description": "Timeout in seconds. Default 60.", "minimum": 1, "maximum": 600 }
      },
      "required": ["operation"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    // Allow so the AgentLoop passes through; the real check is done internally under the
    // effective name (git_read or git_write).
    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    // The settings table shows git_read / git_write, not this facade.
    public bool ExposedInSettings => false;

    // Deferred so the schema is discovered on demand rather than living in the always-on
    // prompt prefix.
    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "operation", out var op, out var opErr))
            return ToolResult.Error(opErr);
        op = op.Trim();

        if (!GitSubcommands.TryClassify(op, out var isWrite))
            return ToolResult.Error(
                $"git operation '{op}' is not in the curated list. " +
                $"Read: {GitSubcommands.ReadList}. Write: {GitSubcommands.WriteList}. " +
                "For anything else, use execute_command.");

        IReadOnlyList<string> extra = Array.Empty<string>();
        if (arguments.TryGetProperty("arguments", out _) &&
            !ToolArguments.TryGetStringArray(arguments, "arguments", out extra, out var argsErr))
            return ToolResult.Error(argsErr);

        var timeoutSeconds = 60;
        if (arguments.TryGetProperty("timeout_seconds", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            timeoutSeconds = Math.Clamp(tProp.GetInt32(), 1, 600);

        var effectiveName = isWrite ? "git_write" : "git_read";

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

        return await GitProcess.RunAsync(op, extra, context.Workspace.RepositoryRoot, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
    }
}
