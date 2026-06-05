using System.Diagnostics;
using System.Text;

namespace Yamca.Agent.Tools.ScriptExecution;

/// <summary>
/// Shared "start a process, capture stdout/stderr, honor a timeout, summarize" loop
/// used by <c>ScriptRunner</c> (file + inline scripts) and <c>ExecuteCommandTool</c>.
/// Centralizes output capping so every caller behaves identically.
/// </summary>
public static class ProcessRunner
{
    // Cap captured output so a runaway process doesn't blow up the chat context.
    private const int MaxStreamChars = 16_000;

    /// <param name="noun">Used in timeout/cancel messages, e.g. "Script" or "Command".</param>
    /// <param name="maxOutputLines">When set, only the last N lines of each stream are kept.</param>
    /// <param name="suppressOutputOnSuccess">When true and the process exits 0, only the
    /// status is returned (stdout/stderr withheld) to save the LLM's context. Failures and
    /// timeouts always return full output for debugging.</param>
    public static async Task<ToolResult> RunAsync(
        ProcessStartInfo psi,
        int timeoutSeconds,
        int? maxOutputLines,
        string noun,
        CancellationToken cancellationToken,
        bool suppressOutputOnSuccess = false)
    {
        ArgumentNullException.ThrowIfNull(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new OutputSink(maxOutputLines);
        var stderr = new OutputSink(maxOutputLines);
        process.OutputDataReceived += (_, e) => stdout.Append(e.Data);
        process.ErrorDataReceived  += (_, e) => stderr.Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start {noun.ToLowerInvariant()}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var clampedTimeout = Math.Clamp(timeoutSeconds, 1, 600);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(clampedTimeout));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var reason = timeoutCts.IsCancellationRequested ? $"timed out after {clampedTimeout}s" : "cancelled";
            return ToolResult.Error($"{noun} {reason}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        // Flush async output handlers.
        process.WaitForExit();

        if (process.ExitCode == 0 && suppressOutputOnSuccess)
            return ToolResult.Ok("");

        var stdoutText = stdout.ToString();
        var summary = new StringBuilder();
        summary.Append("exit_code: ").Append(process.ExitCode).Append('\n');
        summary.Append("stdout:\n").Append(stdoutText);
        if (stdoutText.Length == 0 || stdoutText[^1] != '\n') summary.Append('\n');
        summary.Append("stderr:\n").Append(stderr.ToString());

        return process.ExitCode == 0
            ? ToolResult.Ok(summary.ToString())
            : ToolResult.Error(summary.ToString());
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Accumulates one stream's output. Without a line cap it behaves like a
    /// char-capped buffer (legacy behavior). With a line cap it keeps only the last N
    /// lines — build/test failures cluster at the end of the output.</summary>
    private sealed class OutputSink
    {
        private readonly int? _maxLines;
        private readonly StringBuilder _sb = new();      // no line cap
        private readonly Queue<string> _ring = new();    // line cap set
        private bool _droppedEarlierLines;

        public OutputSink(int? maxLines) => _maxLines = maxLines is > 0 ? maxLines : null;

        public void Append(string? line)
        {
            if (line is null) return;

            if (_maxLines is int max)
            {
                // Bound a single pathological line so the ring can't blow up memory.
                var capped = line.Length > MaxStreamChars ? line[..MaxStreamChars] + "…[truncated]" : line;
                _ring.Enqueue(capped);
                while (_ring.Count > max)
                {
                    _ring.Dequeue();
                    _droppedEarlierLines = true;
                }
                return;
            }

            var remaining = MaxStreamChars - _sb.Length;
            if (remaining <= 0) return;
            if (line.Length + 1 <= remaining)
            {
                _sb.Append(line).Append('\n');
            }
            else
            {
                _sb.Append(line, 0, Math.Min(line.Length, remaining));
                _sb.Append("\n…[truncated]\n");
            }
        }

        public override string ToString()
        {
            if (_maxLines is null) return _sb.ToString();

            var sb = new StringBuilder();
            if (_droppedEarlierLines) sb.Append("…[earlier output truncated]\n");
            foreach (var l in _ring) sb.Append(l).Append('\n');

            // Final safety cap on total chars, keeping the tail (most relevant lines).
            if (sb.Length > MaxStreamChars)
                return "…[truncated]\n" + sb.ToString(sb.Length - MaxStreamChars, MaxStreamChars);
            return sb.ToString();
        }
    }
}
