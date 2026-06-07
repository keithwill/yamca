using Yamca.Agent.Tools.ProcessManagement;

namespace Yamca.Web.Services;

/// <summary>Bridges the process-wide <see cref="BackgroundProcessManager"/> (which lives in
/// Yamca.Agent and so cannot depend on the hosting abstractions) into the app lifecycle. Its only
/// job is to stop every running background process gracefully when Yamca shuts down, so started dev
/// servers are not orphaned. Phase 2 will also launch the project autostart list in <see cref="StartAsync"/>.</summary>
public sealed class BackgroundProcessHost : IHostedService
{
    private readonly BackgroundProcessManager _manager;

    public BackgroundProcessHost(BackgroundProcessManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _manager.StopAllAsync(cancellationToken);
}
