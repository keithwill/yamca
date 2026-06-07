using System.Diagnostics;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>
/// Process-wide singleton that owns long-lived background child processes. Mirrors the
/// lock / snapshot / <c>Changed</c>-event shape of <c>SubagentSessionRegistry</c>. Builds each
/// process's <see cref="ProcessStartInfo"/> through <see cref="ShellResolver.BuildCommandStartInfo"/>
/// — the same single source of truth <c>execute_command</c> uses — so background processes honor the
/// configured shell. The shell preference is supplied per call (resolved by the scoped tool from
/// session settings), keeping this singleton free of scoped dependencies.
/// </summary>
public sealed class BackgroundProcessManager : IBackgroundProcessManager, IAsyncDisposable
{
    // How long to wait for a process to exit on its own (after its stop_command, or before/after a
    // kill) before giving up. Graceful-stop budget per the design.
    private const int GraceSeconds = 5;

    private readonly ShellResolver _shells;
    private readonly object _gate = new();
    private readonly List<BackgroundProcess> _processes = new();

    public BackgroundProcessManager(ShellResolver shells)
    {
        ArgumentNullException.ThrowIfNull(shells);
        _shells = shells;
    }

    public event Action? Changed;

    public IReadOnlyList<BackgroundProcess> Snapshot()
    {
        lock (_gate) return _processes.AsEnumerable().Reverse().ToArray();
    }

    public BackgroundProcess? Get(string name)
    {
        lock (_gate) return _processes.FirstOrDefault(p => p.Name == name);
    }

    public StartOutcome Start(StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var existing = _processes.FirstOrDefault(p => p.Name == request.Name);
            if (existing is not null)
            {
                if (existing.Status == ProcessStatus.Running)
                    return new StartOutcome(existing, AlreadyRunning: true);
                // Replace a dead entry with the same name so the new run takes its place.
                _processes.Remove(existing);
            }
        }

        var shell = _shells.Resolve(request.ShellPreference);
        var bp = new BackgroundProcess(request, shell.Kind);

        var psi = _shells.BuildCommandStartInfo(request.Command, request.WorkingDirectory, request.ShellPreference);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { bp.Append(e.Data, isError: false); Changed?.Invoke(); } };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { bp.Append(e.Data, isError: true);  Changed?.Invoke(); } };
        process.Exited += (_, _) => OnExited(bp, process);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            bp.MarkStartFailed(ex.Message);
            lock (_gate) _processes.Add(bp);
            Changed?.Invoke();
            return new StartOutcome(bp, AlreadyRunning: false);
        }

        bp.Bind(process, process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate) _processes.Add(bp);
        Changed?.Invoke();
        return new StartOutcome(bp, AlreadyRunning: false);
    }

    public OutputSnapshot? GetOutput(string name, long? sinceSeq)
    {
        var bp = Get(name);
        return bp?.ReadOutput(sinceSeq);
    }

    public async Task<bool> Stop(string name)
    {
        var bp = Get(name);
        if (bp is null) return false;
        await StopProcess(bp).ConfigureAwait(false);
        return true;
    }

    public async Task<BackgroundProcess?> Restart(string name)
    {
        var bp = Get(name);
        if (bp is null) return null;

        var request = bp.Request;
        await StopProcess(bp).ConfigureAwait(false);
        lock (_gate) _processes.Remove(bp);
        return Start(request).Process;
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        List<BackgroundProcess> running;
        lock (_gate) running = _processes.Where(p => p.Status == ProcessStatus.Running).ToList();
        await Task.WhenAll(running.Select(StopProcess)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnExited(BackgroundProcess bp, Process process)
    {
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        bp.MarkExited(code);
        Changed?.Invoke();
    }

    /// <summary>Graceful stop: run the optional <c>stop_command</c>, give the process the grace
    /// period to exit on its own, then force-kill the whole tree. Every step is best-effort so a
    /// wedged process can never throw out of shutdown.</summary>
    private async Task StopProcess(BackgroundProcess bp)
    {
        var process = bp.Underlying;
        if (process is null || bp.Status != ProcessStatus.Running) return;

        if (!string.IsNullOrWhiteSpace(bp.StopCommand))
        {
            try
            {
                var psi = _shells.BuildCommandStartInfo(bp.StopCommand!, bp.WorkingDirectory, bp.Request.ShellPreference);
                await ProcessRunner.RunAsync(psi, GraceSeconds, maxOutputLines: 50, "Stop command", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* best-effort */ }

            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(GraceSeconds)).ConfigureAwait(false))
                return;
        }

        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone, or access denied — nothing more we can do */ }

        await WaitForExitAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>Wait up to <paramref name="timeout"/> for exit. Returns true if the process has
    /// exited (or was already gone), false on timeout.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            // Disposed / never started — treat as exited.
            return true;
        }
    }
}
