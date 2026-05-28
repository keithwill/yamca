using NUnit.Framework;
using Yamca.Agent.Mcp;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Mcp;

[TestFixture]
public class McpServerConfigJsonTests
{
    [Test]
    public void DeserializeList_AcceptsWrappedShape()
    {
        var json = """
        [
          {
            "id": "filesystem",
            "enabled": true,
            "config": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/work"],
              "env": { "FOO": "bar" }
            }
          }
        ]
        """;

        var list = McpServerConfigJson.DeserializeList(json);

        Assert.That(list, Has.Count.EqualTo(1));
        var cfg = list[0];
        Assert.That(cfg.Id, Is.EqualTo("filesystem"));
        Assert.That(cfg.Enabled, Is.True);
        Assert.That(cfg.Stdio.Command, Is.EqualTo("npx"));
        Assert.That(cfg.Stdio.Args, Is.EqualTo(new[] { "-y", "@modelcontextprotocol/server-filesystem", "/work" }));
        Assert.That(cfg.Stdio.Env!["FOO"], Is.EqualTo("bar"));
    }

    [Test]
    public void DeserializeList_SkipsDuplicateIds()
    {
        var json = """
        [
          { "id": "a", "config": { "command": "x" } },
          { "id": "a", "config": { "command": "y" } }
        ]
        """;

        var list = McpServerConfigJson.DeserializeList(json);

        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Stdio.Command, Is.EqualTo("x"));
    }

    [Test]
    public void DeserializeList_EmptyOrInvalidReturnsEmpty()
    {
        Assert.That(McpServerConfigJson.DeserializeList(null), Is.Empty);
        Assert.That(McpServerConfigJson.DeserializeList(""), Is.Empty);
        Assert.That(McpServerConfigJson.DeserializeList("not json"), Is.Empty);
    }

    [Test]
    public void DeserializeList_DefaultToolAvailability_DefaultsToDeferred()
    {
        var json = """
        [
          { "id": "a", "config": { "command": "x" } }
        ]
        """;

        var list = McpServerConfigJson.DeserializeList(json);

        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].DefaultToolAvailability, Is.EqualTo(Availability.Deferred));
    }

    [Test]
    public void DeserializeList_DefaultToolAvailability_HonorsEager()
    {
        var json = """
        [
          { "id": "a", "defaultToolAvailability": "eager", "config": { "command": "x" } }
        ]
        """;

        var list = McpServerConfigJson.DeserializeList(json);

        Assert.That(list[0].DefaultToolAvailability, Is.EqualTo(Availability.Eager));
    }

    [Test]
    public void Roundtrip_PreservesDefaultToolAvailability()
    {
        var original = new[]
        {
            new McpServerConfig(
                Id: "a", Enabled: true,
                Stdio: new McpStdioConfig("x", System.Array.Empty<string>()),
                Http: null, CallTimeoutSeconds: null,
                DefaultToolAvailability: Availability.Eager),
        };

        var serialized = McpServerConfigJson.SerializeList(original);
        var parsed = McpServerConfigJson.DeserializeList(serialized);

        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].DefaultToolAvailability, Is.EqualTo(Availability.Eager));
    }

    [Test]
    public void ParseSingle_AcceptsBareMcpJsonShape_WithOverrideId()
    {
        var bare = """
        { "command": "uvx", "args": ["mcp-server-fetch"] }
        """;

        var result = McpServerConfigJson.ParseSingle(bare, overrideId: "fetch");

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(result.Config, Is.Not.Null);
        Assert.That(result.Config!.Id, Is.EqualTo("fetch"));
        Assert.That(result.Config.Stdio.Command, Is.EqualTo("uvx"));
        Assert.That(result.Config.Stdio.Args, Is.EqualTo(new[] { "mcp-server-fetch" }));
    }

    [Test]
    public void ParseSingle_WrappedShape_OverrideIdWins()
    {
        var wrapped = """
        { "id": "from-json", "config": { "command": "x" } }
        """;

        var result = McpServerConfigJson.ParseSingle(wrapped, overrideId: "from-dialog");

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(result.Config!.Id, Is.EqualTo("from-dialog"));
    }

    [Test]
    public void ParseSingle_RejectsMissingCommand()
    {
        var json = """ { "id": "x", "config": { "args": ["a"] } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.MissingCommand));
        Assert.That(result.Config, Is.Null);
    }

    [Test]
    public void ParseSingle_AcceptsHttpTransport_Wrapped()
    {
        var json = """
        { "id": "fetch", "config": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer x" } } }
        """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(result.Config!.TransportKind, Is.EqualTo(McpTransportKind.Http));
        Assert.That(result.Config.Http!.Url, Is.EqualTo("https://example.com/mcp"));
        Assert.That(result.Config.Http.Headers!["Authorization"], Is.EqualTo("Bearer x"));
        Assert.That(result.Config.Stdio, Is.Null);
    }

    [Test]
    public void ParseSingle_AcceptsHttpTransport_Bare()
    {
        var json = """ { "url": "https://example.com/mcp" } """;

        var result = McpServerConfigJson.ParseSingle(json, overrideId: "fetch");

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(result.Config!.Http!.Url, Is.EqualTo("https://example.com/mcp"));
    }

    [Test]
    public void ParseSingle_RejectsHttpWithBothCommandAndUrl()
    {
        var json = """ { "id": "x", "config": { "command": "node", "url": "https://example.com/mcp" } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.UnsupportedTransport));
    }

    [Test]
    public void ParseSingle_RejectsRelativeOrFileUrl()
    {
        var json = """ { "id": "x", "config": { "url": "/relative" } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.InvalidUrl));
    }

    [Test]
    public void ParseSingle_AcceptsTimeoutSecondsOverride()
    {
        var json = """ { "id": "x", "config": { "command": "node", "timeoutSeconds": 90 } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(result.Config!.CallTimeoutSeconds, Is.EqualTo(90));
    }

    [Test]
    public void ParseSingle_RejectsOutOfRangeTimeout()
    {
        var json = """ { "id": "x", "config": { "command": "node", "timeoutSeconds": 0 } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.InvalidTimeout));
    }

    [Test]
    public void SerializeSingle_RoundTripsHttpAndTimeout()
    {
        var original = new McpServerConfig(
            Id: "fetch",
            Enabled: true,
            Stdio: null,
            Http: new McpHttpConfig(
                Url: "https://example.com/mcp",
                Headers: new Dictionary<string, string> { ["X-Api-Key"] = "secret" }),
            CallTimeoutSeconds: 45);

        var json = McpServerConfigJson.SerializeSingle(original);
        var parsed = McpServerConfigJson.ParseSingle(json);

        Assert.That(parsed.Status, Is.EqualTo(McpConfigParseStatus.Ok));
        Assert.That(parsed.Config!.Http!.Url, Is.EqualTo("https://example.com/mcp"));
        Assert.That(parsed.Config.Http.Headers!["X-Api-Key"], Is.EqualTo("secret"));
        Assert.That(parsed.Config.CallTimeoutSeconds, Is.EqualTo(45));
    }

    [Test]
    public void ParseSingle_RejectsBadId()
    {
        var json = """ { "command": "x" } """;

        var result = McpServerConfigJson.ParseSingle(json, overrideId: "has spaces");

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.InvalidId));
    }

    [Test]
    public void ParseSingle_RejectsMissingId()
    {
        var json = """ { "command": "x" } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.MissingId));
    }

    [Test]
    public void SerializeRoundtrip_PreservesAllFields()
    {
        var original = new[]
        {
            new McpServerConfig(
                Id: "test",
                Enabled: false,
                Stdio: new McpStdioConfig(
                    Command: "node",
                    Args: new[] { "index.js" },
                    Env: new Dictionary<string, string> { ["KEY"] = "value" },
                    WorkingDirectory: "/tmp")),
        };

        var json = McpServerConfigJson.SerializeList(original);
        var roundtripped = McpServerConfigJson.DeserializeList(json);

        Assert.That(roundtripped, Has.Count.EqualTo(1));
        var c = roundtripped[0];
        Assert.That(c.Id, Is.EqualTo("test"));
        Assert.That(c.Enabled, Is.False);
        Assert.That(c.Stdio.Command, Is.EqualTo("node"));
        Assert.That(c.Stdio.Args, Is.EqualTo(new[] { "index.js" }));
        Assert.That(c.Stdio.Env!["KEY"], Is.EqualTo("value"));
        Assert.That(c.Stdio.WorkingDirectory, Is.EqualTo("/tmp"));
    }

    [TestCase("a", true)]
    [TestCase("filesystem", true)]
    [TestCase("with-hyphen", true)]
    [TestCase("with_underscore", true)]
    [TestCase("UPPER123", true)]
    [TestCase("", false)]
    [TestCase("has space", false)]
    [TestCase("has/slash", false)]
    [TestCase("has.dot", false)]
    [TestCase("dollar$ign", false)]
    public void IsValidId(string id, bool expected)
    {
        Assert.That(McpServerConfigJson.IsValidId(id), Is.EqualTo(expected));
    }
}
