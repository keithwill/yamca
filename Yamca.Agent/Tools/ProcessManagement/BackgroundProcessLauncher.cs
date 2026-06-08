using System.Text;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Starts a background process and renders the outcome as a <see cref="ToolResult"/>.
/// Shared by <see cref="StartProcessTool"/> and <c>execute_script</c>'s delegation for
/// background-flagged inline commands, so both report a start the same way.</summary>
internal static class BackgroundProcessLauncher
{
    public static ToolResult Start(IBackgroundProcessManager manager, StartRequest request)
    {
        var outcome = manager.Start(request);
        var p = outcome.Process;

        var sb = new StringBuilder();
        if (outcome.AlreadyRunning)
            sb.Append("A process named '").Append(request.Name).Append("' is already running (pid ").Append(p.Pid).Append("); reused it.\n");
        else if (p.Status == ProcessStatus.Failed)
            return ToolResult.Error($"Failed to start '{request.Name}':\n{p.RenderTail()}");
        else
            sb.Append("Started '").Append(request.Name).Append("' (pid ").Append(p.Pid).Append(").\n");

        sb.Append("status: ").Append(p.Status.ToString().ToLowerInvariant());
        return ToolResult.Ok(sb.ToString());
    }
}
