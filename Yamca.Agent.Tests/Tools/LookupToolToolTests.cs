using System.Text.Json;
using NUnit.Framework;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class LookupToolToolTests
{
    // Minimal IServiceProvider so LookupToolTool can resolve the registry + availability resolver
    // the same way it does inside a DI scope.
    private sealed class StubProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();
        public StubProvider Add<T>(T instance) where T : class { _map[typeof(T)] = instance; return this; }
        public object? GetService(Type serviceType) => _map.GetValueOrDefault(serviceType);
    }

    private TempWorkspace _ws = null!;

    [SetUp]
    public void SetUp() => _ws = new TempWorkspace();

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private (LookupToolTool tool, LoadedToolSet loaded) Build(
        DeferredToolsHint hint = DeferredToolsHint.Names)
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new StubTool("read_file"),
            new StubTool("delete_file"),
        });
        var availability = new TestAvailabilityResolver(registry).Set("delete_file", Availability.Deferred);
        var provider = new StubProvider()
            .Add<IToolRegistry>(registry)
            .Add<IAvailabilityResolver>(availability)
            .Add<ISessionSettings>(new InMemorySessionSettings { DeferredToolsHint = hint });
        var loaded = new LoadedToolSet();
        return (new LookupToolTool(provider, loaded), loaded);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: false);

    [Test]
    public async Task NoArguments_ListsDeferredToolsOnly()
    {
        var (tool, _) = Build();

        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("delete_file"));
        Assert.That(result.Content, Does.Not.Contain("read_file"), "eager tools are not part of the deferred catalog");
    }

    [Test]
    public async Task WithName_ReturnsSchemaAndMarksLoaded()
    {
        var (tool, loaded) = Build();

        var result = await tool.ExecuteAsync(Args("""{"tool_names":["delete_file"]}"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("delete_file"));
        Assert.That(result.Content, Does.Contain("properties"), "should include the argument schema");
        Assert.That(loaded.Contains("delete_file"), Is.True);
    }

    [Test]
    public async Task UnknownName_ReportsError()
    {
        var (tool, _) = Build();

        var result = await tool.ExecuteAsync(Args("""{"tool_names":["ghost"]}"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Content, Does.Contain("ghost"));
    }

    [Test]
    public void SessionStartMessage_NamesHint_ListsNamesWithoutDescriptions()
    {
        var (tool, _) = Build(DeferredToolsHint.Names);

        var msg = tool.SessionStartMessage(Ctx());

        Assert.That(msg, Is.Not.Null);
        Assert.That(msg, Does.Contain("delete_file"));
        Assert.That(msg, Does.Contain("lookup_tool"));
        Assert.That(msg, Does.Contain("call_tool"));
        Assert.That(msg, Does.Not.Contain("stub tool delete_file"), "names-only hint must omit descriptions");
    }

    [Test]
    public void SessionStartMessage_NamesAndDescriptionsHint_IncludesDescriptions()
    {
        var (tool, _) = Build(DeferredToolsHint.NamesAndDescriptions);

        var msg = tool.SessionStartMessage(Ctx());

        Assert.That(msg, Does.Contain("stub tool delete_file"), "should include the tool description");
    }

    [Test]
    public void SessionStartMessage_NoHint_KeepsMechanismButOmitsCatalog()
    {
        var (tool, _) = Build(DeferredToolsHint.None);

        var msg = tool.SessionStartMessage(Ctx());

        Assert.That(msg, Is.Not.Null);
        Assert.That(msg, Does.Contain("lookup_tool"), "the model must still learn deferred tools are loadable");
        Assert.That(msg, Does.Contain("call_tool"));
        Assert.That(msg, Does.Not.Contain("delete_file"), "No Hint must omit the per-tool catalog");
    }
}
