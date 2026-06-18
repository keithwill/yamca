using Yamca.Agent.Chat;
using Yamca.Agent.Metrics;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Chat;

/// <summary>Covers the throughput-metric emission added to <see cref="AgentLoop"/>:
/// a record per model round-trip, the Tier-A (server timings) vs Tier-B (wall-clock)
/// distinction, and the rule that a round-trip with no server usage chunk is not recorded.</summary>
[TestFixture]
public class AgentLoopMetricsTests
{
    private TempWorkspace _ws = null!;
    private InMemorySessionSettings _settings = null!;
    private InMemoryPermissionStore _store = null!;
    private ApprovalCoordinator _approvals = null!;
    private FakeChatCompletionClient _llm = null!;
    private ToolRegistry _registry = null!;
    private PermissionResolver _resolver = null!;
    private Yamca.Agent.Tests.Tools.TestAvailabilityResolver _availability = null!;
    private LoadedToolSet _loaded = null!;
    private CollectingSink _sink = null!;
    private readonly Guid _endpointId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _settings = new InMemorySessionSettings();
        _store = new InMemoryPermissionStore(_settings);
        _approvals = new ApprovalCoordinator();
        _llm = new FakeChatCompletionClient();
        _registry = new ToolRegistry(new ITool[] { new StubTool("read_file", PermissionLevel.Allow) });
        _resolver = new PermissionResolver(_registry, _settings);
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loaded = new LoadedToolSet();
        _sink = new CollectingSink();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private AgentLoop NewLoop() => new(
        new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
        new AgentLoopOptions
        {
            MaxIterations = 5,
            OwnerId = "sess-1",
            EndpointId = _endpointId,
            EndpointName = "local",
            Model = "test-model",
        },
        metrics: _sink);

    [Test]
    public async Task ServerTimings_RecordsTierAMetric()
    {
        _llm.Enqueue(new ScriptedResponse(
            "hi", Array.Empty<LlmToolCallRequest>(), "stop",
            Usage: new LlmUsageUpdate(
                PromptTokens: 1000, CompletionTokens: 20, CachedTokens: 128,
                PromptPerSecond: 300, PredictedPerSecond: 40, PromptMs: 333.3, PredictedMs: 500)));

        await Collect(NewLoop().RunTurnAsync("hi"));

        var m = _sink.Records.Single();
        Assert.That(m.TimingsFromServer, Is.True);
        Assert.That(m.PromptTokens, Is.EqualTo(1000));
        Assert.That(m.CompletionTokens, Is.EqualTo(20));
        Assert.That(m.CachedTokens, Is.EqualTo(128));
        Assert.That(m.PromptPerSecond, Is.EqualTo(300));
        Assert.That(m.PredictedPerSecond, Is.EqualTo(40));
        Assert.That(m.EndpointId, Is.EqualTo(_endpointId));
        Assert.That(m.Model, Is.EqualTo("test-model"));
        Assert.That(m.SessionId, Is.EqualTo("sess-1"));
    }

    [Test]
    public async Task UsageWithoutTimings_RecordsTierBMetric()
    {
        _llm.Enqueue(new ScriptedResponse(
            "hello", Array.Empty<LlmToolCallRequest>(), "stop",
            Usage: new LlmUsageUpdate(PromptTokens: 500, CompletionTokens: 10)));

        await Collect(NewLoop().RunTurnAsync("hi"));

        var m = _sink.Records.Single();
        Assert.That(m.TimingsFromServer, Is.False);
        Assert.That(m.PromptTokens, Is.EqualTo(500));
        Assert.That(m.CompletionTokens, Is.EqualTo(10));
        // Wall-clock derived: prompt time is measured between request start and first token.
        Assert.That(m.PromptMs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task RecordMetricsDisabled_RecordsNothing()
    {
        // Even with a full server-timings usage chunk, the loop must not record when the user's
        // "Record throughput metrics" preference (carried as AgentLoopOptions.RecordMetrics) is off.
        _llm.Enqueue(new ScriptedResponse(
            "hi", Array.Empty<LlmToolCallRequest>(), "stop",
            Usage: new LlmUsageUpdate(
                PromptTokens: 1000, CompletionTokens: 20,
                PromptPerSecond: 300, PredictedPerSecond: 40, PromptMs: 333.3, PredictedMs: 500)));

        var loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
            new AgentLoopOptions
            {
                MaxIterations = 5,
                EndpointId = _endpointId,
                EndpointName = "local",
                Model = "test-model",
                RecordMetrics = false,
            },
            metrics: _sink);

        await Collect(loop.RunTurnAsync("hi"));

        Assert.That(_sink.Records, Is.Empty);
    }

    [Test]
    public async Task NoUsageChunk_RecordsNothing()
    {
        // EnqueueText emits no usage event, so there are no real token counts to plot.
        _llm.EnqueueText("hi");

        await Collect(NewLoop().RunTurnAsync("hi"));

        Assert.That(_sink.Records, Is.Empty);
    }

    [Test]
    public async Task ToolLoop_RecordsOneMetricPerRoundTrip()
    {
        // Round-trip 1: a tool call (with usage). Round-trip 2: the final reply (with usage).
        _llm.Enqueue(new ScriptedResponse(
            "", new[] { new LlmToolCallRequest("c1", "read_file", "{}") }, "tool_calls",
            Usage: new LlmUsageUpdate(800, 5, PredictedPerSecond: 35, PromptPerSecond: 250)));
        _llm.Enqueue(new ScriptedResponse(
            "done", Array.Empty<LlmToolCallRequest>(), "stop",
            Usage: new LlmUsageUpdate(900, 8, PredictedPerSecond: 30, PromptPerSecond: 240)));

        await Collect(NewLoop().RunTurnAsync("go"));

        Assert.That(_sink.Records, Has.Count.EqualTo(2));
        Assert.That(_sink.Records[0].PromptTokens, Is.EqualTo(800));
        Assert.That(_sink.Records[1].PromptTokens, Is.EqualTo(900));
    }

    private static async Task<List<ChatStreamEvent>> Collect(IAsyncEnumerable<ChatStreamEvent> events)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var ev in events) list.Add(ev);
        return list;
    }

    private sealed class CollectingSink : ITurnMetricSink
    {
        public List<TurnMetric> Records { get; } = new();
        public void Record(TurnMetric metric) => Records.Add(metric);
    }
}
