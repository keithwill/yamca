namespace Yamca.Agent.Tools.ProcessManagement;

/// <summary>Result of a <see cref="IBackgroundProcessManager.Start"/> call. <see cref="AlreadyRunning"/>
/// is true when a process with the same name was already running, in which case <see cref="Process"/>
/// is that existing handle and nothing new was spawned (dedupe-by-name).</summary>
public sealed record StartOutcome(BackgroundProcess Process, bool AlreadyRunning);

/// <summary>
/// Process-wide owner of long-lived background child processes (dev servers, watchers, workers).
/// One singleton serves every chat session: a process started in one session keeps running after it
/// ends and stays visible to other sessions and the UI. All running processes are stopped gracefully
/// when Yamca shuts down (see <see cref="StopAllAsync"/>).
/// </summary>
public interface IBackgroundProcessManager
{
    /// <summary>Raised after any change (started / output line / exited / stopped). Subscribers on
    /// the UI thread must marshal to the dispatcher themselves — callbacks arrive on pool threads.</summary>
    event Action? Changed;

    /// <summary>A copy of the current process list, newest first.</summary>
    IReadOnlyList<BackgroundProcess> Snapshot();

    /// <summary>The process with the given name, or null.</summary>
    BackgroundProcess? Get(string name);

    /// <summary>Start a process, or return the existing handle when one of the same name is already
    /// running (dedupe-by-name). A dead same-named entry is replaced.</summary>
    StartOutcome Start(StartRequest request);

    /// <summary>Read a process's captured output, or null when no such process exists.</summary>
    OutputSnapshot? GetOutput(string name, long? sinceSeq);

    /// <summary>Stop a process: run its <c>stop_command</c> if set, wait a grace period, then kill the
    /// process tree. Best-effort; returns false only when no such process exists.</summary>
    Task<bool> Stop(string name);

    /// <summary>Stop then re-launch a process with its original request. Null when no such process exists.</summary>
    Task<BackgroundProcess?> Restart(string name);

    /// <summary>Stop every running process gracefully. Called on Yamca shutdown.</summary>
    Task StopAllAsync(CancellationToken cancellationToken);
}
