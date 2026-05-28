using System.Text.Json;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using Yamca.Agent.Mcp;

namespace Yamca.Agent.Tests.Mcp;

[TestFixture]
public class McpToolAdapterTests
{
    [Test]
    public void BuildName_PrefixesWithServerAndDelimiter()
    {
        Assert.That(McpToolAdapter.BuildName("fs", "read_file"), Is.EqualTo("mcp__fs__read_file"));
    }

    [TestCase("mcp__fs__read", true, "fs", "read")]
    [TestCase("mcp__server-1__call_thing", true, "server-1", "call_thing")]
    [TestCase("read_file", false, "", "")]
    [TestCase("mcp__missing", false, "", "")]
    [TestCase("mcp__server__", false, "", "")]
    public void TryParseName(string input, bool expected, string serverId, string toolName)
    {
        var ok = McpToolAdapter.TryParseName(input, out var id, out var name);
        Assert.That(ok, Is.EqualTo(expected));
        if (ok)
        {
            Assert.That(id, Is.EqualTo(serverId));
            Assert.That(name, Is.EqualTo(toolName));
        }
    }

    [Test]
    public void ToToolResult_JoinsTextContentBlocks()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "hello" },
                new TextContentBlock { Text = "world" },
            },
            IsError = false,
        };

        var converted = McpToolAdapter.ToToolResult(result);

        Assert.That(converted.IsError, Is.False);
        Assert.That(converted.Content, Is.EqualTo("hello\nworld"));
    }

    [Test]
    public void ToToolResult_MarksErrorWhenIsErrorTrue()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "boom" } },
            IsError = true,
        };

        var converted = McpToolAdapter.ToToolResult(result);

        Assert.That(converted.IsError, Is.True);
        Assert.That(converted.Content, Is.EqualTo("boom"));
    }

    [Test]
    public void ToToolResult_UsesStructuredContent_WhenNoContentBlocks()
    {
        using var doc = JsonDocument.Parse("""{"answer":42}""");
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>(),
            StructuredContent = doc.RootElement.Clone(),
        };

        var converted = McpToolAdapter.ToToolResult(result);

        Assert.That(converted.IsError, Is.False);
        Assert.That(converted.Content, Does.Contain("\"answer\":42"));
    }

    [Test]
    public void ToToolResult_FallsBackToPlaceholder_WhenEmpty()
    {
        var result = new CallToolResult { Content = new List<ContentBlock>() };
        var converted = McpToolAdapter.ToToolResult(result);
        Assert.That(converted.Content, Is.EqualTo("(no content)"));
    }
}
