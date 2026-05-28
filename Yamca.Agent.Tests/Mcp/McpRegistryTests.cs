using NUnit.Framework;
using Yamca.Agent.Mcp;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Mcp;

[TestFixture]
public class McpRegistryTests
{
    [Test]
    public async Task ReplaceAsync_AddsDisabledServers_AndExposesThemAsServers_ButNotAsTools()
    {
        await using var registry = new McpRegistry();

        var cfg = new McpServerConfig(
            Id: "off",
            Enabled: false,
            Stdio: new McpStdioConfig("does-not-matter", Array.Empty<string>()));

        await registry.ReplaceAsync(new[] { cfg });

        Assert.That(registry.Servers, Has.Count.EqualTo(1));
        Assert.That(registry.Servers[0].Status, Is.EqualTo(McpServerStatus.Disabled));
        Assert.That(registry.Tools, Is.Empty); // disabled never connects, so no tools
        Assert.That(registry.Initialized, Is.True);
    }

    [Test]
    public async Task ReplaceAsync_RaisesChanged()
    {
        await using var registry = new McpRegistry();
        var raised = 0;
        registry.Changed += () => Interlocked.Increment(ref raised);

        await registry.ReplaceAsync(Array.Empty<McpServerConfig>());

        Assert.That(raised, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ReplaceAsync_RemovesServersNotInNewList()
    {
        await using var registry = new McpRegistry();
        var a = new McpServerConfig("a", false, new McpStdioConfig("x", Array.Empty<string>()));
        var b = new McpServerConfig("b", false, new McpStdioConfig("y", Array.Empty<string>()));
        await registry.ReplaceAsync(new[] { a, b });
        Assert.That(registry.Servers, Has.Count.EqualTo(2));

        await registry.ReplaceAsync(new[] { a });

        Assert.That(registry.Servers, Has.Count.EqualTo(1));
        Assert.That(registry.Servers[0].Config.Id, Is.EqualTo("a"));
    }

    [Test]
    public async Task ReplaceAsync_PreservesUnchangedConnections()
    {
        await using var registry = new McpRegistry();
        var cfg = new McpServerConfig("a", false, new McpStdioConfig("x", Array.Empty<string>()));
        await registry.ReplaceAsync(new[] { cfg });
        var firstConnection = registry.Servers[0];

        // Same config — connection instance should be reused.
        await registry.ReplaceAsync(new[] { cfg });

        Assert.That(registry.Servers[0], Is.SameAs(firstConnection));
    }

    [Test]
    public async Task ReplaceAsync_ReplacesConnectionWhenConfigChanges()
    {
        await using var registry = new McpRegistry();
        var original = new McpServerConfig("a", false, new McpStdioConfig("x", new[] { "1" }));
        await registry.ReplaceAsync(new[] { original });
        var firstConnection = registry.Servers[0];

        var changed = original with { Stdio = original.Stdio! with { Args = new[] { "2" } } };
        await registry.ReplaceAsync(new[] { changed });

        Assert.That(registry.Servers[0], Is.Not.SameAs(firstConnection));
    }

    [Test]
    public async Task ReplaceAsync_ReplacesConnectionWhenTimeoutChanges()
    {
        await using var registry = new McpRegistry();
        var original = new McpServerConfig("a", false, new McpStdioConfig("x", Array.Empty<string>()), Http: null, CallTimeoutSeconds: 30);
        await registry.ReplaceAsync(new[] { original });
        var firstConnection = registry.Servers[0];

        var changed = original with { CallTimeoutSeconds = 60 };
        await registry.ReplaceAsync(new[] { changed });

        Assert.That(registry.Servers[0], Is.Not.SameAs(firstConnection));
    }

    [Test]
    public async Task ReplaceAsync_ReplacesConnectionWhenTransportKindChanges()
    {
        await using var registry = new McpRegistry();
        var stdio = new McpServerConfig("a", false, new McpStdioConfig("x", Array.Empty<string>()));
        await registry.ReplaceAsync(new[] { stdio });
        var firstConnection = registry.Servers[0];

        var http = new McpServerConfig("a", false, Stdio: null, Http: new McpHttpConfig("https://example.com/mcp"));
        await registry.ReplaceAsync(new[] { http });

        Assert.That(registry.Servers[0], Is.Not.SameAs(firstConnection));
        Assert.That(registry.Servers[0].Config.TransportKind, Is.EqualTo(McpTransportKind.Http));
    }

    [Test]
    public async Task RestartAsync_ReplacesExistingConnectionInstance()
    {
        await using var registry = new McpRegistry();
        var cfg = new McpServerConfig("a", false, new McpStdioConfig("x", Array.Empty<string>()));
        await registry.ReplaceAsync(new[] { cfg });
        var first = registry.Servers[0];

        var ok = await registry.RestartAsync("a");

        Assert.That(ok, Is.True);
        Assert.That(registry.Servers, Has.Count.EqualTo(1));
        Assert.That(registry.Servers[0], Is.Not.SameAs(first));
    }

    [Test]
    public async Task RestartAsync_UnknownIdReturnsFalse()
    {
        await using var registry = new McpRegistry();
        await registry.ReplaceAsync(Array.Empty<McpServerConfig>());

        var ok = await registry.RestartAsync("nope");

        Assert.That(ok, Is.False);
    }
}

[TestFixture]
public class ToolRegistryDynamicSourceTests
{
    private sealed class StubTool : ITool
    {
        public string Name { get; }
        public StubTool(string name, bool deferred = false) { Name = name; Deferred = deferred; }
        public string Description => "stub";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public bool SupportsWorkspaceRestriction => false;
        public Yamca.Agent.Permissions.PermissionLevel DefaultPermission => Yamca.Agent.Permissions.PermissionLevel.Allow;
        public bool Deferred { get; }
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class StubSource : IDynamicToolSource
    {
        public List<ITool> Tools { get; } = new();
        public IReadOnlyList<ITool> CurrentTools => Tools;
    }

    [Test]
    public void GetChatTools_IncludesDeferredDynamicTool_OnceLoaded()
    {
        var source = new StubSource();
        source.Tools.Add(new StubTool("mcp__fs__read", deferred: true));
        var registry = new ToolRegistry(new ITool[] { new StubTool("static_tool") }, new[] { source });

        var loaded = new Yamca.Agent.Chat.LoadedToolSet();
        var before = registry.GetChatTools(loaded).Select(t => t.Name).ToList();
        Assert.That(before, Does.Not.Contain("mcp__fs__read"));
        Assert.That(before, Does.Contain("static_tool"));

        loaded.MarkLoaded("mcp__fs__read");
        var after = registry.GetChatTools(loaded).Select(t => t.Name).ToList();
        Assert.That(after, Does.Contain("mcp__fs__read"));
    }

    [Test]
    public void Get_ResolvesFromDynamicSource()
    {
        var source = new StubSource();
        source.Tools.Add(new StubTool("mcp__fs__read"));
        var registry = new ToolRegistry(Array.Empty<ITool>(), new[] { source });

        Assert.That(registry.Get("mcp__fs__read"), Is.Not.Null);
        Assert.That(registry.Get("nope"), Is.Null);
    }

    [Test]
    public void GetDeferredTools_IncludesDynamicDeferred()
    {
        var source = new StubSource();
        source.Tools.Add(new StubTool("mcp__fs__read", deferred: true));
        var registry = new ToolRegistry(Array.Empty<ITool>(), new[] { source });

        Assert.That(registry.GetDeferredTools().Select(t => t.Name), Does.Contain("mcp__fs__read"));
    }

    [Test]
    public void StaticTool_WinsAgainstDynamicWithSameName()
    {
        var statique = new StubTool("clash");
        var source = new StubSource();
        source.Tools.Add(new StubTool("clash"));
        var registry = new ToolRegistry(new ITool[] { statique }, new[] { source });

        Assert.That(registry.Get("clash"), Is.SameAs(statique));
        Assert.That(registry.Tools.Count(t => t.Name == "clash"), Is.EqualTo(1));
    }
}
