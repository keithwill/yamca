using NUnit.Framework;
using Yamca.Agent.Metrics;
using Yamca.Agent.Storage;
using Yamca.Agent.Workspace;
using Yamca.Web.Services.Metrics;

namespace Yamca.Web.Tests;

[TestFixture]
public class MetricsQueryServiceTests
{
    private string _root = null!;
    private MetricsStore _store = null!;
    private MetricsQueryService _svc = null!;
    private readonly Guid _epA = Guid.NewGuid();
    private readonly Guid _epB = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "yamca-tests", "metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new MetricsStore(new Workspace(_root, _root));
        _svc = new MetricsQueryService(_store);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static TurnMetric Sample(
        Guid endpointId, string model, int promptTokens,
        double? predicted, double? prompt = 250, bool serverTimings = true,
        string endpointName = "local") =>
        new(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, SessionId: "s",
            EndpointId: endpointId, EndpointName: endpointName, Model: model,
            PromptTokens: promptTokens, CachedTokens: null, CompletionTokens: 10,
            PromptPerSecond: prompt, PredictedPerSecond: predicted, PromptMs: 1, PredictedMs: 1,
            TimingsFromServer: serverTimings);

    [Test]
    public async Task QueryAsync_BucketsByContext_AndMediansSpeeds()
    {
        // Bucket 0 (context < 2000): three samples; median gen speed = 40.
        // Bucket 1 (2000–3999): one sample at 20.
        await _store.RecordManyAsync(new[]
        {
            Sample(_epA, "m", 500, predicted: 30),
            Sample(_epA, "m", 1000, predicted: 40),
            Sample(_epA, "m", 1500, predicted: 50),
            Sample(_epA, "m", 3000, predicted: 20),
        }, CancellationToken.None);

        var view = await _svc.QueryAsync(new MetricsFilter { BucketSize = 2000 }, CancellationToken.None);

        Assert.That(view.TotalSamples, Is.EqualTo(4));
        var series = view.Series.Single();
        Assert.That(series.Buckets, Has.Count.EqualTo(2));

        Assert.That(series.Buckets[0].ContextMidpoint, Is.EqualTo(1000));
        Assert.That(series.Buckets[0].MedianPredictedPerSecond, Is.EqualTo(40));
        Assert.That(series.Buckets[0].Count, Is.EqualTo(3));

        Assert.That(series.Buckets[1].ContextMidpoint, Is.EqualTo(3000));
        Assert.That(series.Buckets[1].MedianPredictedPerSecond, Is.EqualTo(20));
        Assert.That(series.Buckets[1].Count, Is.EqualTo(1));
    }

    [Test]
    public async Task QueryAsync_ServerMeasuredOnly_DropsTierBSamples()
    {
        await _store.RecordManyAsync(new[]
        {
            Sample(_epA, "m", 1000, predicted: 40, serverTimings: true),
            Sample(_epA, "m", 1000, predicted: 999, serverTimings: false),
        }, CancellationToken.None);

        var view = await _svc.QueryAsync(
            new MetricsFilter { ServerMeasuredOnly = true, BucketSize = 2000 }, CancellationToken.None);

        Assert.That(view.TotalSamples, Is.EqualTo(1));
        Assert.That(view.Series.Single().Buckets.Single().MedianPredictedPerSecond, Is.EqualTo(40));
    }

    [Test]
    public async Task QueryAsync_SeriesFilter_LimitsToSelectedEndpointModel()
    {
        await _store.RecordManyAsync(new[]
        {
            Sample(_epA, "m1", 1000, predicted: 40),
            Sample(_epB, "m2", 1000, predicted: 25),
        }, CancellationToken.None);

        var view = await _svc.QueryAsync(
            new MetricsFilter { Series = new[] { new MetricSeriesKey(_epB, "m2") } }, CancellationToken.None);

        Assert.That(view.Series.Single().Key, Is.EqualTo(new MetricSeriesKey(_epB, "m2")));
    }

    [Test]
    public async Task GetSeriesAsync_ReturnsDistinctEndpointModelsWithCounts()
    {
        await _store.RecordManyAsync(new[]
        {
            Sample(_epA, "m1", 1000, predicted: 40),
            Sample(_epA, "m1", 1500, predicted: 42),
            Sample(_epB, "m2", 1000, predicted: 25),
        }, CancellationToken.None);

        var series = await _svc.GetSeriesAsync(CancellationToken.None);

        Assert.That(series, Has.Count.EqualTo(2));
        var a = series.Single(s => s.Key.Equals(new MetricSeriesKey(_epA, "m1")));
        Assert.That(a.SampleCount, Is.EqualTo(2));
    }
}
