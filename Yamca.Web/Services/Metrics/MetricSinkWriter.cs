using System.Threading.Channels;
using Yamca.Agent.Metrics;
using Yamca.Agent.Settings.Persistence;
using Yamca.Agent.Storage;
using Yamca.Web.Services;

namespace Yamca.Web.Services.Metrics;

/// <summary>The production <see cref="ITurnMetricSink"/>: a non-blocking buffer in front of the
/// <see cref="MetricsStore"/>. <see cref="AgentLoop"/> calls <see cref="Record"/> on its hot path,
/// so it must never block or throw — samples are dropped into a bounded channel and a single
/// background drain batches them to disk. The same loop enforces retention periodically (and once
/// at start) so the file can't grow without bound even if the dashboard is never opened.
///
/// Registered as both a singleton (the sink) and a hosted service (the drain lifecycle).</summary>
public sealed class MetricSinkWriter : ITurnMetricSink, IHostedService
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(30);
    private const int MaxBatch = 256;

    private readonly MetricsStore _store;
    // Retention (keep-count + max-age) is a user-tier setting read fresh off the shared user blob
    // each prune, so a change in /preferences takes effect at the next prune without a restart. The
    // singleton writer has no per-circuit SessionSettings, hence the direct store read.
    private readonly UserSettingsStore _userStore;
    private readonly ILogger<MetricSinkWriter> _log;

    // Bounded so a runaway producer can't grow memory unbounded; dropping the oldest unwritten
    // sample under sustained overload is an acceptable loss for best-effort telemetry.
    private readonly Channel<TurnMetric> _channel = Channel.CreateBounded<TurnMetric>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private Task? _drain;

    public MetricSinkWriter(MetricsStore store, UserSettingsStore userStore, ILogger<MetricSinkWriter> log)
    {
        _store = store;
        _userStore = userStore;
        _log = log;
    }

    public void Record(TurnMetric metric) => _channel.Writer.TryWrite(metric);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Don't block app startup; the store opens lazily on the first write.
        _drain = Task.Run(DrainAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Complete the writer so the drain reads to the end of the channel (no lost samples on a
        // graceful stop), bounded by the host's shutdown timeout.
        _channel.Writer.TryComplete();
        if (_drain is not null)
        {
            try { await _drain.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown timed out — drop the remainder */ }
        }
        try { await _store.CloseAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "metrics store close failed during shutdown"); }
    }

    private async Task DrainAsync()
    {
        await PruneSafelyAsync().ConfigureAwait(false);
        var nextPrune = DateTimeOffset.UtcNow + PruneInterval;
        var buffer = new List<TurnMetric>(MaxBatch);

        // ReadAllAsync ends when the writer is completed, after draining everything buffered.
        await foreach (var metric in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            buffer.Add(metric);
            while (buffer.Count < MaxBatch && _channel.Reader.TryRead(out var more))
                buffer.Add(more);

            try { await _store.RecordManyAsync(buffer, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "metrics batch write failed ({Count} samples)", buffer.Count); }
            buffer.Clear();

            if (DateTimeOffset.UtcNow >= nextPrune)
            {
                await PruneSafelyAsync().ConfigureAwait(false);
                nextPrune = DateTimeOffset.UtcNow + PruneInterval;
            }
        }
    }

    private async Task PruneSafelyAsync()
    {
        try
        {
            var retention = SessionSettings.ReadMetricsRetention(_userStore.Load());
            await _store.PruneAsync(retention.MaxSamples, retention.MaxAge, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogDebug(ex, "metrics prune failed"); }
    }
}
