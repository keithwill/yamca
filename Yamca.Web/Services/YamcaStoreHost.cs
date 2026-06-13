using Yamca.Agent.Storage;

namespace Yamca.Web.Services;

/// <summary>Warms the <see cref="YamcaStore"/> on application start (so the first board read doesn't
/// pay the open cost) and, more importantly, closes it on shutdown so VestPocket flushes any pending
/// transactions durably to disk. Open is lazy regardless — this host just bookends the lifetime.</summary>
public sealed class YamcaStoreHost : IHostedService
{
    private readonly YamcaStore _store;

    public YamcaStoreHost(YamcaStore store) => _store = store;

    public async Task StartAsync(CancellationToken cancellationToken)
        => await _store.GetAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken)
        => _store.CloseAsync(cancellationToken);
}
