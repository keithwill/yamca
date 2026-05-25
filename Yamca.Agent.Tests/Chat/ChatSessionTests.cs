using OpenAI.Chat;
using Yamca.Agent.Chat;
using Yamca.Agent.Tests.Support;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class ChatSessionTests
{
    [Test]
    public void SystemPrompt_IsAtIndexZero()
    {
        var session = new ChatSession("you are a test");

        Assert.That(session.Messages, Has.Count.EqualTo(1));
        Assert.That(session.Messages[0], Is.InstanceOf<SystemChatMessage>());
        Assert.That(session.SystemPrompt, Is.EqualTo("you are a test"));
    }

    [Test]
    public void WorkspaceConstructor_KeepsPromptStableAndIncludesWorkspaceContext()
    {
        using var ws = new TempWorkspace();
        var session = new ChatSession(ws.Workspace, "you are a test");

        Assert.That(session.SystemPrompt, Is.EqualTo("you are a test"));
        Assert.That(session.Messages, Has.Count.EqualTo(1));
        Assert.That(session.Messages[0], Is.InstanceOf<SystemChatMessage>());

        var text = ((SystemChatMessage)session.Messages[0]).Content[0].Text;
        Assert.That(text, Does.StartWith("you are a test"));
        Assert.That(text, Does.Contain(ws.RootPath));
    }

    [Test]
    public void InstructionsConstructor_ConcatenatesInstructionsIntoSingleSystemMessage()
    {
        using var ws = new TempWorkspace();
        var instructions = new[] { "# Instructions from A.md\n\nhello", "# Instructions from B.md\n\nworld" };

        var session = new ChatSession(ws.Workspace, "you are a test", instructions);

        Assert.That(session.Messages, Has.Count.EqualTo(1));
        Assert.That(session.Messages[0], Is.InstanceOf<SystemChatMessage>());

        var text = ((SystemChatMessage)session.Messages[0]).Content[0].Text;
        Assert.That(text, Does.StartWith("you are a test"));
        Assert.That(text, Does.Contain(ws.RootPath));
        Assert.That(text, Does.Contain(instructions[0]));
        Assert.That(text, Does.Contain(instructions[1]));
    }

    [Test]
    public void InstructionsConstructor_EmptyList_MatchesWorkspaceOnlyConstructor()
    {
        using var ws = new TempWorkspace();

        var session = new ChatSession(ws.Workspace, "sys", Array.Empty<string>());
        var reference = new ChatSession(ws.Workspace, "sys");

        Assert.That(session.Messages, Has.Count.EqualTo(1));
        Assert.That(((SystemChatMessage)session.Messages[0]).Content[0].Text,
            Is.EqualTo(((SystemChatMessage)reference.Messages[0]).Content[0].Text));
    }

    [Test]
    public void InstructionsConstructor_SkipsWhitespaceEntries()
    {
        using var ws = new TempWorkspace();

        var session = new ChatSession(ws.Workspace, "sys", new[] { "real", " ", "", "also-real" });

        Assert.That(session.Messages, Has.Count.EqualTo(1));
        var text = ((SystemChatMessage)session.Messages[0]).Content[0].Text;
        Assert.That(text, Does.Contain("real"));
        Assert.That(text, Does.Contain("also-real"));
    }

    [Test]
    public void AppendUser_AddsUserMessageInOrder()
    {
        var session = new ChatSession("sys");

        session.AppendUser("hello");
        session.AppendUser("again");

        Assert.That(session.Messages, Has.Count.EqualTo(3));
        Assert.That(session.Messages[1], Is.InstanceOf<UserChatMessage>());
        Assert.That(session.Messages[2], Is.InstanceOf<UserChatMessage>());
    }

    [Test]
    public void AppendAssistant_WithoutToolCalls_StoresAssistantMessage()
    {
        var session = new ChatSession("sys");
        session.AppendAssistant("hi back", Array.Empty<LlmToolCallRequest>());

        Assert.That(session.Messages[^1], Is.InstanceOf<AssistantChatMessage>());
    }

    [Test]
    public void AppendAssistant_WithToolCalls_RoundTripsCallMetadata()
    {
        var session = new ChatSession("sys");
        session.AppendAssistant(
            content: "running tool",
            toolCalls: new[] { new LlmToolCallRequest("call_1", "read_file", """{"path":"a"}""") });

        var asst = (AssistantChatMessage)session.Messages[^1];
        Assert.That(asst.ToolCalls, Has.Count.EqualTo(1));
        Assert.That(asst.ToolCalls[0].Id, Is.EqualTo("call_1"));
        Assert.That(asst.ToolCalls[0].FunctionName, Is.EqualTo("read_file"));
        Assert.That(asst.ToolCalls[0].FunctionArguments.ToString(), Is.EqualTo("""{"path":"a"}"""));
    }

    [Test]
    public void AppendToolResult_StoresToolMessageWithCallId()
    {
        var session = new ChatSession("sys");
        session.AppendToolResult("call_1", "result content");

        var tool = (ToolChatMessage)session.Messages[^1];
        Assert.That(tool.ToolCallId, Is.EqualTo("call_1"));
    }
}
