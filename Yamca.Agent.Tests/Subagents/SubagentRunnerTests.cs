using System.Net.Http;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Subagents;

[TestFixture]
public class SubagentRunnerTests
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

    private SubagentRunner BuildRunner(params ITool[] tools)
    {
        var registry = new ToolRegistry(tools);
        var services = new SingleServiceProvider(typeof(IToolRegistry), registry);
        var runner = new SubagentRunner(_settings, services, _approvals, new ThrowingHttpClientFactory());
        runner.Bind(_llm);
        return runner;
    }

    private ToolContext ParentContext(bool restrict = false) => new(_ws.Workspace, restrict);

    private void DefineAgent(SubagentDefinition def) =>
        _settings.UserSubagents = new SubagentRegistry(new[] { def });

    private static SubagentDefinition Agent(
        string name,
        IReadOnlyList<string> allowedTools,
        bool restrict = true,
        bool requireApproval = false) =>
        new(Guid.NewGuid(), name, "desc", "Do the task.", allowedTools,
            RestrictToWorkspace: restrict, RequireApproval: requireApproval);

    [Test]
    public async Task DeliversResult_WhenSubagentCallsSubagentResult()
    {
        DefineAgent(Agent("explorer", new[] { "read_file" }));
        var runner = BuildRunner(new StubTool("read_file"));
        _llm.EnqueueToolCall("c1", "subagent_result", """{"result":"the answer"}""");

        var result = await runner.RunAsync("explorer", "find X", ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Is.EqualTo("the answer"));
    }

    [Test]
    public async Task ReturnsError_WhenSubagentNeverDelivers()
    {
        DefineAgent(Agent("explorer", new[] { "read_file" }));
        var runner = BuildRunner(new StubTool("read_file"));
        _llm.EnqueueText("I am not sure what you mean.");

        var result = await runner.RunAsync("explorer", "find X", ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("did not return a result"));
        Assert.That(result.Content, Does.Contain("I am not sure"));
    }

    [Test]
    public async Task ReturnsError_ForUnknownAgent_ListingAvailable()
    {
        DefineAgent(Agent("explorer", new[] { "read_file" }));
        var runner = BuildRunner(new StubTool("read_file"));

        var result = await runner.RunAsync("nope", "do it", ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("Unknown subagent 'nope'"));
        Assert.That(result.Content, Does.Contain("explorer"));
        Assert.That(_llm.Calls, Is.Empty);
    }

    [Test]
    public async Task SandboxesRestrictableTools_PerSubagentSetting()
    {
        var probe = new RecordingRestrictTool("probe");
        DefineAgent(Agent("explorer", new[] { "probe" }, restrict: true));
        var runner = BuildRunner(probe);

        _llm.EnqueueToolCall("c1", "probe", "{}");
        _llm.EnqueueToolCall("c2", "subagent_result", """{"result":"done"}""");

        var result = await runner.RunAsync("explorer", "probe it", ParentContext(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(probe.LastRestrictToWorkspace, Is.True);
    }

    [Test]
    public async Task DoesNotSandbox_WhenSubagentRestrictionOff()
    {
        var probe = new RecordingRestrictTool("probe");
        DefineAgent(Agent("explorer", new[] { "probe" }, restrict: false));
        var runner = BuildRunner(probe);

        _llm.EnqueueToolCall("c1", "probe", "{}");
        _llm.EnqueueToolCall("c2", "subagent_result", """{"result":"done"}""");

        await runner.RunAsync("explorer", "probe it", ParentContext(restrict: true), CancellationToken.None);

        Assert.That(probe.LastRestrictToWorkspace, Is.False);
    }

    [Test]
    public async Task RequireApproval_DeniesRun_WhenUserDeclines()
    {
        DefineAgent(Agent("publisher", new[] { "read_file" }, requireApproval: true));
        var runner = BuildRunner(new StubTool("read_file"));

        // Decline the approval as soon as it is queued.
        var consumer = Task.Run(async () =>
        {
            await foreach (var req in _approvals.Pending.ReadAllAsync())
            {
                req.Deny();
                break;
            }
        });

        var result = await runner.RunAsync("publisher", "ship it", ParentContext(), CancellationToken.None);
        await consumer;

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("was not approved"));
        Assert.That(_llm.Calls, Is.Empty); // never reached the model
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

    /// <summary>Records the workspace-restriction flag it was handed, and declares that it
    /// supports restriction so the resolver actually applies the subagent's setting.</summary>
    private sealed class RecordingRestrictTool : ITool
    {
        public RecordingRestrictTool(string name) => Name = name;

        public bool? LastRestrictToWorkspace { get; private set; }

        public string Name { get; }
        public string Description => "probe";
        public string ParametersSchema => """{ "type":"object", "properties":{}, "additionalProperties": true }""";
        public bool SupportsWorkspaceRestriction => true;
        public PermissionLevel DefaultPermission => PermissionLevel.Allow;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            LastRestrictToWorkspace = context.RestrictToWorkspace;
            return Task.FromResult(ToolResult.Ok("probed"));
        }
    }
}
