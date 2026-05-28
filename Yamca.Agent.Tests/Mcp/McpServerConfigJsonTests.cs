using NUnit.Framework;
using Yamca.Agent.Mcp;

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
    public void ParseSingle_RejectsHttpTransport_AsUnsupportedInPhase1()
    {
        var json = """ { "id": "fetch", "config": { "url": "https://example.com/mcp" } } """;

        var result = McpServerConfigJson.ParseSingle(json);

        Assert.That(result.Status, Is.EqualTo(McpConfigParseStatus.UnsupportedTransport));
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
