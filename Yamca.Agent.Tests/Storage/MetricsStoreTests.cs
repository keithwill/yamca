using Yamca.Agent.Metrics;
using Yamca.Agent.Storage;

namespace Yamca.Agent.Tests.Storage;

[TestFixture]
public class MetricsStoreTests
{
    private MetricsStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new MetricsStore(filePath: null); // in-memory

    [TearDown]
    public async Task TearDown() => await _store.DisposeAsync();

    private static TurnMetric Sample(string id, DateTimeOffset ts, int promptTokens = 100) =>
        new(
            Id: id, TimestampUtc: ts, SessionId: "s", EndpointId: Guid.Empty, EndpointName: "local",
            Model: "m", PromptTokens: promptTokens, CachedTokens: null, CompletionTokens: 10,
            PromptPerSecond: 300, PredictedPerSecond: 40, PromptMs: 1, PredictedMs: 1,
            TimingsFromServer: true);

    [Test]
    public async Task RecordAndQuery_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordManyAsync(new[] { Sample("a", now, 100), Sample("b", now, 200) }, CancellationToken.None);

        var all = await _store.QueryAsync(CancellationToken.None);

        Assert.That(all.Select(m => m.PromptTokens), Is.EquivalentTo(new[] { 100, 200 }));
    }

    [Test]
    public async Task Prune_OverCap_DropsOldestBeyondKeepMax()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var samples = Enumerable.Range(0, 5)
            .Select(i => Sample($"id{i}", t0.AddMinutes(i), promptTokens: i))
            .ToArray();
        await _store.RecordManyAsync(samples, CancellationToken.None);

        var deleted = await _store.PruneAsync(keepMax: 2, maxAge: null, CancellationToken.None);

        Assert.That(deleted, Is.EqualTo(3));
        var remaining = (await _store.QueryAsync(CancellationToken.None)).Select(m => m.PromptTokens);
        // The two newest (largest minute offsets) survive.
        Assert.That(remaining, Is.EquivalentTo(new[] { 3, 4 }));
    }

    [Test]
    public async Task Prune_MaxAge_DropsOldSamples()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordManyAsync(new[]
        {
            Sample("old", now.AddHours(-3), 1),
            Sample("fresh", now.AddMinutes(-1), 2),
        }, CancellationToken.None);

        var deleted = await _store.PruneAsync(keepMax: 100, maxAge: TimeSpan.FromHours(1), CancellationToken.None);

        Assert.That(deleted, Is.EqualTo(1));
        var remaining = await _store.QueryAsync(CancellationToken.None);
        Assert.That(remaining.Single().PromptTokens, Is.EqualTo(2));
    }

    [Test]
    public async Task ClearAll_RemovesEverything()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordManyAsync(new[] { Sample("a", now), Sample("b", now) }, CancellationToken.None);

        await _store.ClearAllAsync(CancellationToken.None);

        Assert.That(await _store.QueryAsync(CancellationToken.None), Is.Empty);
    }
}
