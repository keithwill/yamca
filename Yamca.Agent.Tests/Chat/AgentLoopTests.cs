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
    private Yamca.Agent.Tests.Tools.TestAvailabilityResolver _availability = null!;
    private LoadedToolSet _loaded = null!;
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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loaded = new LoadedToolSet();

        var session = new ChatSession("sys");
        _loop = new AgentLoop(
            session, _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded);

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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded);

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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded);

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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded);

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
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry);
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
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
    public async Task ContinueTurnAsync_ResumesCappedTurnWithoutNewUserMessage()
    {
        _loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
            new AgentLoopOptions { MaxIterations = 1 });

        // First turn: one tool call, then the cap is hit before the model can reply.
        _llm.EnqueueToolCall("c1", "read_file", "{}");
        var first = await Collect(_loop.RunTurnAsync("go"));
        Assert.That(first.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.MaxIterationsReached));
        var messagesAfterFirst = _loop.Session.Messages.Count;

        // Continue: the model now produces a plain reply. No user message is appended.
        _llm.EnqueueText("all done");
        var resumed = await Collect(_loop.ContinueTurnAsync());

        Assert.That(resumed.OfType<AssistantMessageEvent>().Single().Content, Is.EqualTo("all done"));
        Assert.That(resumed.OfType<TurnCompleteEvent>().Single().Reason,
            Is.EqualTo(TurnCompletionReason.AssistantReply));
        // Exactly one assistant message added by the continuation — no extra user turn.
        Assert.That(_loop.Session.Messages, Has.Count.EqualTo(messagesAfterFirst + 1));
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

    // --- call_tool dispatcher -------------------------------------------------------

    // Builds a loop whose registry contains a single deferred tool, reachable only via call_tool.
    private (AgentLoop loop, StubTool inner) NewDeferredLoop(
        string name = "secret_tool", PermissionLevel permission = PermissionLevel.Allow)
    {
        var inner = new StubTool(name, permission);
        _registry = new ToolRegistry(new ITool[] { inner });
        _resolver = new PermissionResolver(_registry, _settings);
        _availability = new Yamca.Agent.Tests.Tools.TestAvailabilityResolver(_registry).Set(name, Availability.Deferred);
        _loaded = new LoadedToolSet();
        var loop = new AgentLoop(
            new ChatSession("sys"), _llm, _registry, _resolver, _availability, _approvals, _store, _ws.Workspace, _loaded,
            new AgentLoopOptions { MaxIterations = 5 });
        return (loop, inner);
    }

    [Test]
    public async Task CallTool_FirstDispatch_ReturnsSchemaWithoutExecuting()
    {
        var (loop, inner) = NewDeferredLoop();
        _llm.EnqueueToolCall("c1", "call_tool", """{"name":"secret_tool","arguments":{"x":1}}""");
        _llm.EnqueueText("ok");

        var events = await Collect(loop.RunTurnAsync("go"));

        Assert.That(inner.Invocations, Is.Empty, "first dispatch must not execute the tool");
        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ToolName, Is.EqualTo("secret_tool"), "UI should reference the real tool, not call_tool");
        Assert.That(result.Content, Does.Contain("secret_tool"));
        Assert.That(_loaded.Contains("secret_tool"), Is.True, "schema return should mark the tool loaded");
    }

    [Test]
    public async Task CallTool_AfterLookup_ExecutesInnerToolWithRealNameAndArgs()
    {
        var (loop, inner) = NewDeferredLoop();
        _loaded.MarkLoaded("secret_tool"); // simulate a prior lookup_tool
        _llm.EnqueueToolCall("c1", "call_tool", """{"name":"secret_tool","arguments":{"x":1}}""");
        _llm.EnqueueText("done");

        var events = await Collect(loop.RunTurnAsync("go"));

        Assert.That(inner.Invocations, Has.Count.EqualTo(1));
        Assert.That(events.OfType<ToolCallStartedEvent>().Single().ToolName, Is.EqualTo("secret_tool"));
        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.False);
        Assert.That(result.ToolName, Is.EqualTo("secret_tool"));
    }

    [Test]
    public async Task CallTool_UnknownInnerTool_ReportsError()
    {
        var (loop, _) = NewDeferredLoop();
        _llm.EnqueueToolCall("c1", "call_tool", """{"name":"ghost"}""");
        _llm.EnqueueText("ok");

        var events = await Collect(loop.RunTurnAsync("go"));

        var denied = events.OfType<ToolDeniedEvent>().Single();
        Assert.That(denied.ToolName, Is.EqualTo("ghost"));
        Assert.That(denied.Reason, Does.Contain("lookup_tool"));
    }

    [Test]
    public async Task CallTool_OnEagerTool_ReportsMisuseWithoutExecuting()
    {
        // _tool ("read_file") is eager; dispatching it through call_tool is a misuse.
        _llm.EnqueueToolCall("c1", "call_tool", """{"name":"read_file","arguments":{}}""");
        _llm.EnqueueText("ok");

        var events = await Collect(_loop.RunTurnAsync("go"));

        Assert.That(_tool.Invocations, Is.Empty);
        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("call it directly"));
    }

    [Test]
    public async Task DirectCallToDeferredTool_RedirectsToCallTool()
    {
        var (loop, inner) = NewDeferredLoop();
        _loaded.MarkLoaded("secret_tool");
        _llm.EnqueueToolCall("c1", "secret_tool", "{}"); // bypassing the dispatcher
        _llm.EnqueueText("ok");

        var events = await Collect(loop.RunTurnAsync("go"));

        Assert.That(inner.Invocations, Is.Empty);
        var denied = events.OfType<ToolDeniedEvent>().Single();
        Assert.That(denied.ToolName, Is.EqualTo("secret_tool"));
        Assert.That(denied.Reason, Does.Contain("call_tool"));
    }

    [Test]
    public async Task CallTool_PermissionResolvesAgainstInnerTool()
    {
        // The deferred tool is denied via settings; the dispatcher must apply that to the inner
        // tool's name (not "call_tool") and refuse to execute.
        var (loop, inner) = NewDeferredLoop("danger_tool");
        _loaded.MarkLoaded("danger_tool");
        _settings.Project = new Yamca.Agent.Settings.ToolSettingsMap(
            new Dictionary<string, Yamca.Agent.Settings.ToolPermissionSettings>
            {
                ["danger_tool"] = new() { Permission = PermissionLevel.Deny },
            });

        _llm.EnqueueToolCall("c1", "call_tool", """{"name":"danger_tool","arguments":{}}""");
        _llm.EnqueueText("ack");

        var events = await Collect(loop.RunTurnAsync("go"));

        Assert.That(inner.Invocations, Is.Empty);
        Assert.That(events.OfType<ToolDeniedEvent>().Single().ToolName, Is.EqualTo("danger_tool"));
    }

    [Test]
    public async Task CallTool_InvalidArgumentsJson_ReportsError()
    {
        var (loop, _) = NewDeferredLoop();
        _llm.EnqueueToolCall("c1", "call_tool", "{not json");
        _llm.EnqueueText("ok");

        var events = await Collect(loop.RunTurnAsync("go"));

        var result = events.OfType<ToolCallResultEvent>().Single();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("call_tool"));
    }

    private static async Task<List<ChatStreamEvent>> Collect(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var ev in stream) list.Add(ev);
        return list;
    }
}
