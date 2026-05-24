using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class AgentLoopTests
{
    private TempWorkspace _ws = null!;
    private InMemorySessionSettings _settings = null!;
    private InMemoryPermissionStore _store = null!;
    private ApprovalCoordinator _approvals = null!;
    private FakeChatCompletionClient _llm = null!;
    private StubTool _tool = null!;
    private ToolRegistry _registry = null!;
    private PermissionResolver _resolver = null!;
    private AgentLoop _loop = null!;

    [SetUp]
    public void SetUp()
    {
        _ws = new TempWorkspace();
        _settings = new InMemorySessionSettings();
        _store = new InMemoryPermissionStore(_settings);
        _approvals = new ApprovalCoordinator();
        _llm = new FakeChatCompletionClient();
        _tool = new StubTool("read_file", PermissionLevel.Allow);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _resolver = new PermissionResolver(_registry, _settings);

        var session = new ChatSession("sys");
        _loop = new AgentLoop(
            session, _llm, _registry, _resolver, _approvals, _store, _ws.Workspace,
            new AgentLoopOptions { MaxIterations = 5 });
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    [Test]
    public async Task PlainAssistantReply_CompletesTurnInOneIteration()
    {
        _llm.EnqueueText("hello there");

        var events = await Collect(_loop.RunTurnAsync("hi"));

        Assert.That(events.OfType<AssistantTokenEvent>().Single().Delta, Is.EqualTo("hello there"));
        Assert.That(events.OfType<AssistantMessageEvent>().Single().Content, Is.EqualTo("hello there"));
        Assert.That(events.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.AssistantReply));
        Assert.That(_llm.Calls, Has.Count.EqualTo(1));
        Assert.That(_loop.Session.Messages, Has.Count.EqualTo(3)); // system, user, assistant
    }

    [Test]
    public async Task ToolCall_ThenFinalReply_RunsToolAndAppendsResultThenFinishes()
    {
        _llm.EnqueueToolCall("call_1", "read_file", """{"path":"x"}""");
        _llm.EnqueueText("done");

        var events = await Collect(_loop.RunTurnAsync("read x"));

        Assert.That(_tool.Invocations, Has.Count.EqualTo(1));
        Assert.That(events.OfType<ToolCallStartedEvent>().Single().ToolName, Is.EqualTo("read_file"));
        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Is.EqualTo("read_file ok"));
        Assert.That(events.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.AssistantReply));
        Assert.That(_llm.Calls, Has.Count.EqualTo(2));

        // History should contain: system, user, assistant(toolcall), tool, assistant(final)
        Assert.That(_loop.Session.Messages, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task AskTool_ApprovedAtRuntime_Executes()
    {
        _tool = new StubTool("write_file", PermissionLevel.Ask);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _resolver = new PermissionResolver(_registry, _settings);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _approvals, _store, _ws.Workspace);

        _llm.EnqueueToolCall("c1", "write_file", """{"path":"a"}""");
        _llm.EnqueueText("ok");

        var runTask = Task.Run(() => Collect(_loop.RunTurnAsync("write")));

        var pending = await _approvals.Pending.ReadAsync();
        Assert.That(pending.ToolName, Is.EqualTo("write_file"));
        pending.Approve(ApprovalPersistence.None);

        var events = await runTask;
        Assert.That(_tool.Invocations, Has.Count.EqualTo(1));
        Assert.That(events.OfType<ToolCallResultEvent>().Single().IsError, Is.False);
        Assert.That(_store.Writes, Is.Empty);
    }

    [Test]
    public async Task AskTool_DeniedAtRuntime_SkipsExecutionAndReportsToModel()
    {
        _tool = new StubTool("write_file", PermissionLevel.Ask);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _resolver = new PermissionResolver(_registry, _settings);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _approvals, _store, _ws.Workspace);

        _llm.EnqueueToolCall("c1", "write_file", "{}");
        _llm.EnqueueText("understood");

        var runTask = Task.Run(() => Collect(_loop.RunTurnAsync("write")));

        var pending = await _approvals.Pending.ReadAsync();
        pending.Deny(ApprovalPersistence.None);

        var events = await runTask;
        Assert.That(_tool.Invocations, Is.Empty);
        var denied = events.OfType<ToolDeniedEvent>().Single();
        Assert.That(denied.ToolName, Is.EqualTo("write_file"));
    }

    [Test]
    public async Task AskTool_ApprovedWithProjectPersistence_WritesBackAndSecondCallSkipsPrompt()
    {
        _tool = new StubTool("write_file", PermissionLevel.Ask);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _resolver = new PermissionResolver(_registry, _settings);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _approvals, _store, _ws.Workspace);

        _llm.EnqueueToolCall("c1", "write_file", "{}");
        _llm.EnqueueToolCall("c2", "write_file", "{}");
        _llm.EnqueueText("all done");

        var runTask = Task.Run(() => Collect(_loop.RunTurnAsync("two writes")));

        var pending = await _approvals.Pending.ReadAsync();
        pending.Approve(ApprovalPersistence.Project);

        var events = await runTask;

        Assert.That(_tool.Invocations, Has.Count.EqualTo(2));
        Assert.That(_store.Writes, Has.Count.EqualTo(1));
        Assert.That(_store.Writes[0].Tier, Is.EqualTo(ApprovalPersistence.Project));
        Assert.That(_settings.Project.Get("write_file")?.Permission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(events.OfType<ToolCallResultEvent>().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task DeniedTool_FromSettings_FailsClosedWithoutApprovalPrompt()
    {
        _tool = new StubTool("write_file", PermissionLevel.Ask);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _settings.Project = new Yamca.Agent.Settings.ToolSettingsMap(
            new Dictionary<string, Yamca.Agent.Settings.ToolPermissionSettings>
            {
                ["write_file"] = new() { Permission = PermissionLevel.Deny },
            });
        _resolver = new PermissionResolver(_registry, _settings);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _approvals, _store, _ws.Workspace);

        _llm.EnqueueToolCall("c1", "write_file", "{}");
        _llm.EnqueueText("ack");

        var events = await Collect(_loop.RunTurnAsync("write"));

        Assert.That(_tool.Invocations, Is.Empty);
        Assert.That(events.OfType<ToolDeniedEvent>(), Has.One.Items);
    }

    [Test]
    public async Task MaxIterations_TerminatesInfiniteToolLoop()
    {
        _tool = new StubTool("read_file", PermissionLevel.Allow);
        _registry = new ToolRegistry(new ITool[] { _tool });
        _resolver = new PermissionResolver(_registry, _settings);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _approvals, _store, _ws.Workspace,
            new AgentLoopOptions { MaxIterations = 3 });

        for (var i = 0; i < 5; i++)
            _llm.EnqueueToolCall($"c{i}", "read_file", "{}");

        var events = await Collect(_loop.RunTurnAsync("loop"));

        Assert.That(_llm.Calls, Has.Count.EqualTo(3));
        Assert.That(events.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.MaxIterationsReached));
        Assert.That(_llm.PendingResponses, Is.EqualTo(2)); // two unused responses remain
    }

    [Test]
    public async Task UnknownTool_ReportsErrorAndContinues()
    {
        _llm.EnqueueToolCall("c1", "does_not_exist", "{}");
        _llm.EnqueueText("ok");

        var events = await Collect(_loop.RunTurnAsync("call ghost"));

        Assert.That(events.OfType<ToolDeniedEvent>().Single().ToolName, Is.EqualTo("does_not_exist"));
        Assert.That(events.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.AssistantReply));
    }

    [Test]
    public async Task InvalidJsonArguments_ReportsErrorWithoutExecutingTool()
    {
        _llm.EnqueueToolCall("c1", "read_file", "{not json");
        _llm.EnqueueText("ok");

        var events = await Collect(_loop.RunTurnAsync("malformed"));

        Assert.That(_tool.Invocations, Is.Empty);
        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Invalid JSON"));
    }

    [Test]
    public async Task ToolExecutionException_IsCapturedAsErrorResult()
    {
        _tool.Responder = (_, _) => throw new InvalidOperationException("boom");

        _llm.EnqueueToolCall("c1", "read_file", "{}");
        _llm.EnqueueText("noted");

        var events = await Collect(_loop.RunTurnAsync("crash"));

        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("boom"));
    }

    private static async Task<List<ChatStreamEvent>> Collect(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var ev in stream) list.Add(ev);
        return list;
    }
}
