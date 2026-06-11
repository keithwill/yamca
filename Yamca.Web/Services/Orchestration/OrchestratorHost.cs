namespace Yamca.Web.Services.Orchestration;

/// <summary>Runs the orchestrator's poll loop for the process lifetime (modeled on
/// <see cref="BackgroundProcessHost"/>). The loop always ticks — reconciliation and message
/// draining must continue even while dispatch is disabled — and shutdown cancels the loop
/// then waits for in-flight runs to stop.</summary>
public sealed class OrchestratorHost : IHostedService
{
    private readonly OrchestratorService _orchestrator;
    private readonly CancellationTokenSource _loopCts = new();
    private Task? _loop;

    public OrchestratorHost(OrchestratorService orchestrator) => _orchestrator = orchestrator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => _orchestrator.RunLoopAsync(_loopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts.Cancel();
        await _orchestrator.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* best-effort drain */ }
        }
    }
}
