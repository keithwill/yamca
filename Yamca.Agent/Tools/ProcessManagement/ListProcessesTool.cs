using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Lists every background process known to the manager (running and exited), with pid,
/// status, declared ports, and uptime. Read-only, so it defaults to Allow.</summary>
public sealed class ListProcessesTool : ITool
{
    private readonly IBackgroundProcessManager _manager;

    public ListProcessesTool(IBackgroundProcessManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public string Name => "list_processes";

    public string Description =>
        "List all background processes (running and exited) with their pid, status, ports, and uptime.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {},
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var processes = _manager.Snapshot();
        if (processes.Count == 0)
            return Task.FromResult(ToolResult.Ok("No background processes."));

        var now = DateTimeOffset.Now;
        var sb = new StringBuilder();
        foreach (var p in processes)
        {
            var status = p.Status.ToString().ToLowerInvariant();
            if (p.ExitCode is int code && p.Status != ProcessStatus.Running) status += $" (exit {code})";

            var elapsed = (p.ExitedAt ?? now) - p.StartedAt;
            sb.Append("- ").Append(p.Name)
              .Append("  pid=").Append(p.Pid?.ToString() ?? "-")
              .Append("  status=").Append(status)
              .Append("  uptime=").Append(FormatDuration(elapsed));
            if (p.Ports.Count > 0)
                sb.Append("  ports=").Append(string.Join(",", p.Ports));
            sb.Append("\n    ").Append(p.Command).Append('\n');
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 0) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h{d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m{d.Seconds}s";
        return $"{d.Seconds}s";
    }
}
