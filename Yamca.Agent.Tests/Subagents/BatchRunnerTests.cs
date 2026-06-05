using System.Net.Http;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Subagents;

[TestFixture]
public class BatchRunnerTests
{
    private TempWorkspace _ws = null!;
    private InMemorySessionSettings _settings = null!;
    private ApprovalCoordinator _approvals = null!;
    private FakeChatCompletionClient _llm = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _settings = new InMemorySessionSettings();
        _approvals = new ApprovalCoordinator();
        _llm = new FakeChatCompletionClient();
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private BatchRunner BuildBatch(params ITool[] tools)
    {
        var registry = new ToolRegistry(tools);
        var services = new SingleServiceProvider(typeof(IToolRegistry), registry);
        var runner = new SubagentRunner(_settings, services, _approvals, new ThrowingHttpClientFactory());
        runner.Bind(_llm);
        return new BatchRunner(runner, _settings);
    }

    private void DefineAgent() =>
        _settings.UserSubagents = new SubagentRegistry(new[]
        {
            new SubagentDefinition(Guid.NewGuid(), "analyze", "desc", "Do the task.",
                new[] { "read_file" }, RestrictToWorkspace: false),
        });

    private ToolContext ParentContext() => new(_ws.Workspace, false);

    [Test]
    public async Task ReducesMixedStatuses_WithCountsAndSections()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));

        // One response per item, in order.
        _llm.EnqueueToolCall("c1", "subagent_result", """{"status":"success","result":"all good"}""");
        _llm.EnqueueToolCall("c2", "subagent_result", """{"status":"needs_followup","result":"check this one"}""");
        _llm.EnqueueToolCall("c3", "subagent_result", """{"status":"failure","result":"could not parse"}""");
        _llm.EnqueueText("I give up"); // item 4: never delivers -> mechanical failure
        _llm.EnqueueText("still giving up"); // item 4 ignores the one-shot nudge too

        var items = new[] { "a.md", "b.md", "c.md", "d.md" };
        var result = await batch.RunAsync("analyze", "Analyze it.", items, ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("Loop over 4 items with agent 'analyze': 1 success, 1 needs_followup, 2 failed."));
        Assert.That(result.Content, Does.Contain("needs_followup:"));
        Assert.That(result.Content, Does.Contain("check this one"));
        Assert.That(result.Content, Does.Contain("could not parse"));
        Assert.That(result.Content, Does.Contain("did not return a result")); // mechanical failure tail
        Assert.That(result.Content, Does.Contain("a.md"));
    }

    [Test]
    public async Task AppendsItemToPrompt()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));
        _llm.EnqueueToolCall("c1", "subagent_result", """{"status":"success","result":"done"}""");

        await batch.RunAsync("analyze", "Analyze this.", new[] { "auth.md" }, ParentContext(), CancellationToken.None);

        // The subagent's user message is the per-item prompt: template + appended item.
        var userText = string.Join("\n", _llm.Calls[0].Messages.Select(m => m.Content));
        Assert.That(userText, Does.Contain("Analyze this."));
        Assert.That(userText, Does.Contain("Item: auth.md"));
    }

    [Test]
    public async Task InterpolatesItemPlaceholder_WithoutAppending()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));
        _llm.EnqueueToolCall("c1", "subagent_result", """{"status":"success","result":"done"}""");

        await batch.RunAsync("analyze", "Check {{item}} for the name.", new[] { "auth.md" }, ParentContext(), CancellationToken.None);

        var userText = string.Join("\n", _llm.Calls[0].Messages.Select(m => m.Content));
        Assert.That(userText, Does.Contain("Check auth.md for the name."));
        Assert.That(userText, Does.Not.Contain("{{item}}"));
        Assert.That(userText, Does.Not.Contain("Item: auth.md")); // substituted, not appended
    }

    [Test]
    public async Task InterpolatesSingleBracePlaceholder()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));
        _llm.EnqueueToolCall("c1", "subagent_result", """{"status":"success","result":"done"}""");

        await batch.RunAsync("analyze", "File: {item}", new[] { "doc/worktrees.md" }, ParentContext(), CancellationToken.None);

        var userText = string.Join("\n", _llm.Calls[0].Messages.Select(m => m.Content));
        Assert.That(userText, Does.Contain("File: doc/worktrees.md"));
        Assert.That(userText, Does.Not.Contain("{item}"));
        Assert.That(userText, Does.Not.Contain("Item: doc/worktrees.md"));
    }

    [Test]
    public async Task RejectsEmptyItems()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));

        var result = await batch.RunAsync("analyze", "p", Array.Empty<string>(), ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("non-empty 'items'"));
        Assert.That(_llm.Calls, Is.Empty);
    }

    [Test]
    public async Task RejectsTooManyItems()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));
        var items = Enumerable.Range(0, BatchRunner.MaxItems + 1).Select(i => $"f{i}.md").ToArray();

        var result = await batch.RunAsync("analyze", "p", items, ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("at most"));
        Assert.That(_llm.Calls, Is.Empty);
    }

    [Test]
    public async Task RejectsUnknownAgent_BeforeRunningAnything()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));

        var result = await batch.RunAsync("nope", "p", new[] { "a.md" }, ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Unknown subagent 'nope'"));
        Assert.That(_llm.Calls, Is.Empty);
    }

    [Test]
    public async Task Cancellation_SurfacesPartialResults()
    {
        DefineAgent();
        var batch = BuildBatch(new StubTool("read_file"));
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled: no item runs

        var result = await batch.RunAsync("analyze", "p", new[] { "a.md", "b.md" }, ParentContext(), cts.Token);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("Cancelled after 0 of 2."));
        Assert.That(_llm.Calls, Is.Empty);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("A bound runner must not build its own HttpClient.");
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly Type _type;
        private readonly object _instance;

        public SingleServiceProvider(Type type, object instance)
        {
            _type = type;
            _instance = instance;
        }

        public object? GetService(Type serviceType) => serviceType == _type ? _instance : null;
    }
}
