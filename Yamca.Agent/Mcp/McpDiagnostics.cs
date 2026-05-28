using System.Diagnostics;
using System.Text;
using Yamca.Agent.Tools.ScriptExecution;

namespace Yamca.Agent.Mcp;

/// <summary>
/// One-shot probes for the configured stdio command. The settings UI uses this
/// to triage "server fails to start" — by far the most common stdio failure on
/// Windows is the command itself not being on PATH, or being a shim that needs
/// PATHEXT resolution.
/// </summary>
public static class McpDiagnostics
{
    public sealed record DiagnosticsResult(
        bool Success,
        string ResolvedCommand,
        int? ExitCode,
        string Stdout,
        string Stderr,
        string? Error,
        TimeSpan Elapsed);

    public static async Task<DiagnosticsResult> ProbeStdioAsync(
        McpStdioConfig stdio,
        string probeArg = "--version",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdio);

        var resolved = ResolveCommand(stdio.Command);
        if (resolved is null)
        {
            return new DiagnosticsResult(
                Success: false,
                ResolvedCommand: stdio.Command,
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                Error: $"Command '{stdio.Command}' was not found on PATH.",
                Elapsed: TimeSpan.Zero);
        }

        var psi = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = stdio.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(probeArg);
        if (stdio.Env is not null)
        {
            foreach (var kv in stdio.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new DiagnosticsResult(false, resolved, null, string.Empty, string.Empty,
                    "Process.Start returned null.", sw.Elapsed);
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new DiagnosticsResult(false, resolved, null,
                    await SafeReadAsync(stdoutTask).ConfigureAwait(false),
                    await SafeReadAsync(stderrTask).ConfigureAwait(false),
                    $"Probe timed out after {(timeout ?? TimeSpan.FromSeconds(8)).TotalSeconds:F0}s.",
                    sw.Elapsed);
            }

            var stdout = TrimOutput(await stdoutTask.ConfigureAwait(false));
            var stderr = TrimOutput(await stderrTask.ConfigureAwait(false));
            return new DiagnosticsResult(
                Success: proc.ExitCode == 0,
                ResolvedCommand: resolved,
                ExitCode: proc.ExitCode,
                Stdout: stdout,
                Stderr: stderr,
                Error: null,
                Elapsed: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new DiagnosticsResult(false, resolved, null, string.Empty, string.Empty,
                ex.Message, sw.Elapsed);
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return TrimOutput(await task.ConfigureAwait(false)); }
        catch { return string.Empty; }
    }

    private static string TrimOutput(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        const int max = 4000;
        if (s.Length > max) s = s.Substring(0, max) + "\n…(truncated)";
        return s.TrimEnd();
    }

    private static string? ResolveCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        if (Path.IsPathRooted(command)) return command;
        if (command.Contains('/', StringComparison.Ordinal) || command.Contains('\\', StringComparison.Ordinal))
            return command;
        var resolver = new InterpreterResolver();
        return resolver.Resolve(new[] { command });
    }

    public static string FormatForLog(DiagnosticsResult r)
    {
        var sb = new StringBuilder();
        sb.Append("probe '").Append(r.ResolvedCommand).Append("' → ");
        if (r.Error is not null) sb.Append("error: ").Append(r.Error);
        else if (r.ExitCode is { } code) sb.Append("exit ").Append(code);
        sb.Append(" (").Append(r.Elapsed.TotalMilliseconds.ToString("F0")).Append("ms)");
        return sb.ToString();
    }
}
