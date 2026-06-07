using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Reads the buffered stdout/stderr of a background process. Read-only, so it defaults to
/// Allow. Pass the <c>next_cursor</c> from a previous call as <c>since</c> to poll incrementally.</summary>
public sealed class GetProcessOutputTool : ITool
{
    private readonly IBackgroundProcessManager _manager;

    public GetProcessOutputTool(IBackgroundProcessManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public string Name => "get_process_output";

    public string Description =>
        "Read the captured stdout/stderr of a background process started with start_process. " +
        "Pass the next_cursor from a previous call as 'since' to fetch only new output.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "name":  { "type": "string", "description": "The process name passed to start_process." },
        "since": { "type": "integer", "description": "Cursor from a previous call's next_cursor. Omit to read the retained tail." }
      },
      "required": ["name"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "name", out var name, out var nameError))
            return Task.FromResult(ToolResult.Error(nameError));

        long? since = null;
        if (arguments.TryGetProperty("since", out var sinceProp) && sinceProp.ValueKind == JsonValueKind.Number)
            since = sinceProp.GetInt64();

        var process = _manager.Get(name);
        if (process is null)
            return Task.FromResult(ToolResult.Error($"No background process named '{name}'."));

        var output = process.ReadOutput(since);
        var sb = new StringBuilder();
        sb.Append("status: ").Append(process.Status.ToString().ToLowerInvariant());
        if (process.ExitCode is int code) sb.Append(" (exit_code ").Append(code).Append(')');
        sb.Append('\n');
        sb.Append("next_cursor: ").Append(output.NextCursor).Append('\n');
        sb.Append("output:\n");
        if (output.Truncated) sb.Append("…[earlier output truncated]\n");
        foreach (var line in output.Lines) sb.Append(line.Text).Append('\n');

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }
}
