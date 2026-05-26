using System.Diagnostics;
using System.Text;

namespace Yamca.Agent.Tools.ScriptExecution;

/// <summary>
/// Shared execution helper for the two script tools. Dispatches by extension to the
/// correct interpreter, runs the process with no shell in the loop, and returns a
/// <see cref="ToolResult"/> whose Content matches <c>ExecuteCommandTool</c>'s shape.
/// </summary>
public sealed class ScriptRunner
{
    // Cap captured output so a runaway script doesn't blow up the chat context.
    private const int MaxStreamChars = 16_000;

    private readonly InterpreterResolver _interpreters;

    public ScriptRunner(InterpreterResolver interpreters)
    {
        ArgumentNullException.ThrowIfNull(interpreters);
        _interpreters = interpreters;
    }

    public async Task<ToolResult> RunAsync(
        string resolvedScriptPath,
        IReadOnlyList<string> userArguments,
        int timeoutSeconds,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(resolvedScriptPath);
        ArgumentNullException.ThrowIfNull(userArguments);
        ArgumentNullException.ThrowIfNull(context);

        if (!File.Exists(resolvedScriptPath))
            return ToolResult.Error($"Script '{resolvedScriptPath}' does not exist or is not a regular file.");

        var dispatchResult = BuildDispatch(resolvedScriptPath);
        if (dispatchResult.Error is not null)
            return ToolResult.Error(dispatchResult.Error);

        var psi = new ProcessStartInfo
        {
            FileName = dispatchResult.FileName!,
            WorkingDirectory = context.Workspace.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in dispatchResult.InterpreterArgs) psi.ArgumentList.Add(arg);
        if (dispatchResult.PassScriptPathArg) psi.ArgumentList.Add(resolvedScriptPath);
        foreach (var arg in userArguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived  += (_, e) => AppendCapped(stderr, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start script: {ex.Message}");
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
            return ToolResult.Error($"Script {reason}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        process.WaitForExit();

        var summary = new StringBuilder();
        summary.Append("exit_code: ").Append(process.ExitCode).Append('\n');
        summary.Append("stdout:\n").Append(stdout.ToString());
        if (stdout.Length == 0 || stdout[^1] != '\n') summary.Append('\n');
        summary.Append("stderr:\n").Append(stderr.ToString());

        return process.ExitCode == 0
            ? ToolResult.Ok(summary.ToString())
            : ToolResult.Error(summary.ToString());
    }

    /// <summary>Produces a human-readable preview of what would be dispatched for a script.
    /// Used by the approval UI for discovered scripts before the user grants permission.</summary>
    public string DescribeDispatch(string resolvedScriptPath)
    {
        var dispatch = BuildDispatch(resolvedScriptPath);
        if (dispatch.Error is not null) return dispatch.Error;

        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(dispatch.FileName!));
        foreach (var a in dispatch.InterpreterArgs) sb.Append(' ').Append(Quote(a));
        if (dispatch.PassScriptPathArg) sb.Append(' ').Append(Quote(resolvedScriptPath));
        return sb.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        return arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
    }

    private Dispatch BuildDispatch(string resolvedScriptPath)
    {
        var ext = Path.GetExtension(resolvedScriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1"  => Powershell(),
            ".sh"   => Shell(resolvedScriptPath),
            ".py"   => Python(),
            ".js"   => Node(),
            ".mjs"  => Node(),
            ".ts"   => Typescript(),
            ""      => Dispatch.Fail($"Cannot run '{Path.GetFileName(resolvedScriptPath)}': no file extension. Supported: .ps1 .sh .py .js .mjs .ts."),
            _       => Dispatch.Fail($"Cannot run '{Path.GetFileName(resolvedScriptPath)}': unsupported extension '{ext}'. Supported: .ps1 .sh .py .js .mjs .ts."),
        };
    }

    private Dispatch Powershell()
    {
        var exe = _interpreters.Resolve(new[] { "pwsh" })
                  ?? (OperatingSystem.IsWindows() ? _interpreters.Resolve(new[] { "powershell" }) : null);
        if (exe is null)
            return Dispatch.Fail("Cannot run .ps1 script: neither 'pwsh' nor 'powershell' found on PATH.");
        return new Dispatch(exe, new[] { "-NoProfile", "-File" }, PassScriptPathArg: true);
    }

    private Dispatch Shell(string resolvedScriptPath)
    {
        if (OperatingSystem.IsWindows())
            return Dispatch.Fail("Cannot run .sh script on Windows. Install WSL or convert to .ps1.");

        // Honor the kernel's shebang handling when the file is marked executable.
        if (IsExecutable(resolvedScriptPath))
            return new Dispatch(resolvedScriptPath, Array.Empty<string>(), PassScriptPathArg: false);

        var sh = _interpreters.Resolve(new[] { "sh" }) ?? "/bin/sh";
        return new Dispatch(sh, Array.Empty<string>(), PassScriptPathArg: true);
    }

    private Dispatch Python()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python", "py" }
            : new[] { "python3", "python" };
        var exe = _interpreters.Resolve(candidates);
        if (exe is null)
            return Dispatch.Fail($"Cannot run .py script: none of [{string.Join(", ", candidates)}] found on PATH.");
        return new Dispatch(exe, Array.Empty<string>(), PassScriptPathArg: true);
    }

    private Dispatch Node()
    {
        var exe = _interpreters.Resolve(new[] { "node" });
        if (exe is null)
            return Dispatch.Fail("Cannot run .js / .mjs script: 'node' not found on PATH.");
        return new Dispatch(exe, Array.Empty<string>(), PassScriptPathArg: true);
    }

    private Dispatch Typescript()
    {
        var candidates = new[] { "tsx", "ts-node", "bun", "deno" };
        var exe = _interpreters.Resolve(candidates);
        if (exe is null)
            return Dispatch.Fail("Cannot run .ts script: none of [tsx, ts-node, bun, deno] found on PATH.");

        var name = Path.GetFileNameWithoutExtension(exe);
        return string.Equals(name, "deno", StringComparison.OrdinalIgnoreCase)
            ? new Dispatch(exe, new[] { "run" }, PassScriptPathArg: true)
            : new Dispatch(exe, Array.Empty<string>(), PassScriptPathArg: true);
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendCapped(StringBuilder sink, string? line)
    {
        if (line is null) return;
        var remaining = MaxStreamChars - sink.Length;
        if (remaining <= 0) return;

        if (line.Length + 1 <= remaining)
        {
            sink.Append(line).Append('\n');
        }
        else
        {
            sink.Append(line, 0, Math.Min(line.Length, remaining));
            sink.Append("\n…[truncated]\n");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    private readonly record struct Dispatch(string? FileName, IReadOnlyList<string> InterpreterArgs, bool PassScriptPathArg, string? Error = null)
    {
        public static Dispatch Fail(string error) => new(null, Array.Empty<string>(), false, error);
    }
}
