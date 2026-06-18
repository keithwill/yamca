using Yamca.Agent.Metrics;
using Yamca.Agent.Storage;

namespace Yamca.Web.Services.Metrics;

/// <summary>Identifies one plotted line: a unique endpoint·model pair. The name is a denormalized
/// snapshot from the samples (the endpoint may since have been renamed or deleted).</summary>
public sealed record MetricSeriesKey(Guid EndpointId, string Model)
{
    /// <summary>Human label for the series, tolerating any blank combination — an OpenAI-style
    /// endpoint legitimately carries no model, and an unnamed endpoint no name. When neither a name
    /// nor a model is known, the endpoint's base URL (if captured) is used: it's something the user
    /// actually configured and recognizes, unlike a bare endpoint id which is surfaced nowhere in
    /// the app. Falls back to a short endpoint id only when even the URL is unknown (e.g. samples
    /// recorded before the URL was snapshotted).</summary>
    public string Label(string endpointName, string? endpointBaseUrl = null)
    {
        var name = endpointName?.Trim() ?? "";
        var model = Model?.Trim() ?? "";
        if (name.Length > 0 && model.Length > 0) return $"{name} · {model}";
        if (name.Length > 0) return name;
        if (model.Length > 0) return model;
        var url = endpointBaseUrl?.Trim() ?? "";
        if (url.Length > 0) return url;
        return EndpointId == Guid.Empty ? "(unknown endpoint)" : $"endpoint {EndpointId.ToString("N")[..8]}";
    }
}

/// <summary>A series available to chart, with how many samples it has and its time span — drives
/// the dashboard's series picker.</summary>
public sealed record MetricSeriesInfo(
    MetricSeriesKey Key, string EndpointName, string EndpointBaseUrl, int SampleCount,
    DateTimeOffset FirstUtc, DateTimeOffset LastUtc);

/// <summary>One context-size bucket on a series: the median prompt-processing and token-generation
/// speeds for the samples whose starting context fell in this bucket. Medians (not means) so a few
/// outliers — a cold cache, a paused server — don't distort the curve.</summary>
public sealed record MetricBucket(
    int ContextMidpoint, double? MedianPromptPerSecond, double? MedianPredictedPerSecond, int Count);

public sealed record MetricSeriesData(MetricSeriesKey Key, string EndpointName, string EndpointBaseUrl, IReadOnlyList<MetricBucket> Buckets);

public sealed record MetricsView(
    IReadOnlyList<MetricSeriesData> Series, int TotalSamples,
    DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc);

public sealed record MetricsFilter
{
    /// <summary>The series to include; null/empty means every series.</summary>
    public IReadOnlyCollection<MetricSeriesKey>? Series { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>When true, keep only Tier-A samples (the server reported authoritative timings),
    /// dropping the noisier client wall-clock estimates.</summary>
    public bool ServerMeasuredOnly { get; init; }

    /// <summary>Width, in prompt tokens, of each context bucket on the X axis.</summary>
    public int BucketSize { get; init; } = 2000;
}

/// <summary>Reads <see cref="TurnMetric"/> samples from the <see cref="MetricsStore"/> and shapes
/// them for the throughput dashboard: it enumerates the available endpoint·model series and, for a
/// given filter, buckets samples by starting context size and reduces each bucket to median speeds.
/// Bucketing turns a noisy per-sample scatter into clean degradation curves that the (deliberately
/// simple) MudChart line component can render.</summary>
public sealed class MetricsQueryService
{
    private readonly MetricsStore _store;

    public MetricsQueryService(MetricsStore store) => _store = store;

    public long FileSizeBytes => _store.FileSizeBytes;

    /// <summary>The distinct endpoint·model series present, newest-active first.</summary>
    public async Task<IReadOnlyList<MetricSeriesInfo>> GetSeriesAsync(CancellationToken ct)
    {
        var all = await _store.QueryAsync(ct).ConfigureAwait(false);
        return all
            .GroupBy(m => new MetricSeriesKey(m.EndpointId, m.Model))
            .Select(g => new MetricSeriesInfo(
                g.Key,
                // Prefer a non-empty snapshot name; fall back to the most recent one seen.
                g.OrderByDescending(m => m.TimestampUtc).Select(m => m.EndpointName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? g.OrderByDescending(m => m.TimestampUtc).First().EndpointName,
                LatestNonEmptyBaseUrl(g),
                g.Count(),
                g.Min(m => m.TimestampUtc),
                g.Max(m => m.TimestampUtc)))
            .OrderByDescending(s => s.LastUtc)
            .ToList();
    }

    /// <summary>Filter, bucket, and reduce the samples into ready-to-plot series.</summary>
    public async Task<MetricsView> QueryAsync(MetricsFilter filter, CancellationToken ct)
    {
        var bucketSize = Math.Max(1, filter.BucketSize);
        var wanted = filter.Series is { Count: > 0 }
            ? new HashSet<MetricSeriesKey>(filter.Series)
            : null;

        var all = await _store.QueryAsync(ct).ConfigureAwait(false);

        var filtered = all.Where(m =>
            (filter.From is not { } f || m.TimestampUtc >= f) &&
            (filter.To is not { } t || m.TimestampUtc <= t) &&
            (!filter.ServerMeasuredOnly || m.TimingsFromServer) &&
            (wanted is null || wanted.Contains(new MetricSeriesKey(m.EndpointId, m.Model))))
            .ToList();

        var series = filtered
            .GroupBy(m => new MetricSeriesKey(m.EndpointId, m.Model))
            .Select(g =>
            {
                var endpointName = g.OrderByDescending(m => m.TimestampUtc)
                    .Select(m => m.EndpointName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";

                var buckets = g
                    .GroupBy(m => m.PromptTokens / bucketSize)
                    .OrderBy(b => b.Key)
                    .Select(b => new MetricBucket(
                        ContextMidpoint: b.Key * bucketSize + bucketSize / 2,
                        MedianPromptPerSecond: Median(b.Select(m => m.PromptPerSecond)),
                        MedianPredictedPerSecond: Median(b.Select(m => m.PredictedPerSecond)),
                        Count: b.Count()))
                    .ToList();

                return new MetricSeriesData(g.Key, endpointName, LatestNonEmptyBaseUrl(g), buckets);
            })
            .OrderBy(s => s.EndpointName)
            .ThenBy(s => s.Key.Model)
            .ToList();

        return new MetricsView(
            series,
            filtered.Count,
            filtered.Count == 0 ? null : filtered.Min(m => m.TimestampUtc),
            filtered.Count == 0 ? null : filtered.Max(m => m.TimestampUtc));
    }

    public Task ClearAllAsync(CancellationToken ct) => _store.ClearAllAsync(ct);

    /// <summary>The most recent non-blank base-URL snapshot in the group, or "" when none carry one
    /// (e.g. every sample predates the URL field). Denormalized like the endpoint name.</summary>
    private static string LatestNonEmptyBaseUrl(IEnumerable<TurnMetric> group) =>
        group.OrderByDescending(m => m.TimestampUtc)
             .Select(m => m.EndpointBaseUrl)
             .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "";

    /// <summary>Median of the non-null values, or null when none are present.</summary>
    private static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
