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
    public void WorkspaceConstructor_KeepsPromptStableAndAppendsWorkspaceContext()
    {
        using var ws = new TempWorkspace();
        var session = new ChatSession(ws.Workspace, "you are a test");

        Assert.That(session.SystemPrompt, Is.EqualTo("you are a test"),
            "User-authored system prompt must stay byte-identical so it can be prompt-cached across sessions and workspaces.");
        Assert.That(session.Messages, Has.Count.EqualTo(2));
        Assert.That(session.Messages[0], Is.InstanceOf<SystemChatMessage>());
        Assert.That(session.Messages[1], Is.InstanceOf<SystemChatMessage>());

        var workspaceMessage = (SystemChatMessage)session.Messages[1];
        Assert.That(workspaceMessage.Content[0].Text, Does.Contain(ws.RootPath));
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
