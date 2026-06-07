using System.Diagnostics;
using System.Text;

namespace Yamca.Agent.Tools.Git;

/// <summary>Runs a single curated git subcommand. Mirrors <c>GitService</c>'s
/// invocation model: the <c>git</c> executable is launched directly with arguments supplied
/// as an argv list and <c>UseShellExecute = false</c>, so shell metacharacters in the model's
/// arguments (<c>;</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>$()</c>, backticks) are inert. The
/// model also cannot inject a leading <c>-c</c> before the subcommand, since the subcommand's
/// position in argv is fixed here.</summary>
internal static class GitProcess
{
    public static async Task<ToolResult> RunAsync(
        string operation, IReadOnlyList<string> extra, string workingDir, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // --no-pager is a global git option (legal before the subcommand); it stops any
        // configured pager from being spawned (a pager can exec arbitrary commands) and
        // prevents a pager from blocking on output.
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add(operation);
        foreach (var a in extra) psi.ArgumentList.Add(a);

        // Never block on a credential prompt; read ops don't take index locks.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return ToolResult.Error($"failed to launch git: {ex.Message}"); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            if (ct.IsCancellationRequested) throw;
            return ToolResult.Error($"git {operation} timed out after {timeoutSeconds}s.");
        }

        var body = stdout.ToString();
        if (stderr.Length > 0) body += (body.Length > 0 ? "\n" : "") + stderr;
        return proc.ExitCode == 0
            ? ToolResult.Ok(body.Length == 0 ? $"git {operation} succeeded (no output)." : body)
            : ToolResult.Error($"git {operation} exited {proc.ExitCode}:\n{body}");
    }
}
