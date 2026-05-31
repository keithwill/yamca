using Yamca.Agent.Chat;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class ChatSessionRestoreTests
{
    private static List<ChatMessage> SampleLog() => new()
    {
        new ChatMessage(ChatRole.System, "you are a test"),
        new ChatMessage(ChatRole.User, "hello"),
        new ChatMessage(ChatRole.Assistant, "calling a tool", ToolCalls: new[]
        {
            new ChatToolCall("call-1", "read_file", "{\"path\":\"a.txt\"}"),
        }),
        new ChatMessage(ChatRole.Tool, "file contents", ToolCallId: "call-1"),
        new ChatMessage(ChatRole.Assistant, "all done"),
    };

    [Test]
    public void Restore_PreservesMessagesVerbatim()
    {
        var log = SampleLog();

        var session = ChatSession.Restore(log);

        Assert.That(session.Messages, Has.Count.EqualTo(log.Count));
        Assert.That(session.SystemPrompt, Is.EqualTo("you are a test"));
        Assert.That(session.Messages[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(session.Messages[2].ToolCalls, Is.Not.Null);
        Assert.That(session.Messages[2].ToolCalls![0].Id, Is.EqualTo("call-1"));
        Assert.That(session.Messages[3].ToolCallId, Is.EqualTo("call-1"));
    }

    [Test]
    public void Restore_RecomputesEstimatedTokens()
    {
        var session = ChatSession.Restore(SampleLog());

        // char/4 over all content + tool name/args; must be a positive, non-trivial estimate.
        Assert.That(session.EstimatedInputTokens, Is.GreaterThan(0));
    }

    [Test]
    public void Restore_ContinuesToAcceptNewMessages()
    {
        var session = ChatSession.Restore(SampleLog());
        var before = session.EstimatedInputTokens;

        session.AppendUser("another question");

        Assert.That(session.Messages, Has.Count.EqualTo(6));
        Assert.That(session.Messages[^1].Role, Is.EqualTo(ChatRole.User));
        Assert.That(session.EstimatedInputTokens, Is.GreaterThan(before));
    }

    [Test]
    public void Restore_PreservesCompactedSystemMessageAndFindsKeepIndex()
    {
        // A system message that already carries a compaction summary, plus enough user
        // turns that FindKeepFromIndexForRecentTurns has something to cut.
        var log = new List<ChatMessage>
        {
            new(ChatRole.System, "base prompt\n\n[Summary of earlier conversation]: stuff happened"),
            new(ChatRole.User, "q1"),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "q2"),
            new(ChatRole.Assistant, "a2"),
            new(ChatRole.User, "q3"),
            new(ChatRole.Assistant, "a3"),
        };

        var session = ChatSession.Restore(log);

        Assert.That(session.Messages[0].Content, Does.Contain("[Summary of earlier conversation]"));
        // Keep the most recent 1 user turn → cutoff is the index of "q3" (5), which is >= 2.
        Assert.That(session.FindKeepFromIndexForRecentTurns(1), Is.EqualTo(5));
    }

    [Test]
    public void Restore_RejectsEmptyLog()
    {
        Assert.That(() => ChatSession.Restore(Array.Empty<ChatMessage>()),
            Throws.ArgumentException);
    }

    [Test]
    public void Restore_RejectsLogNotStartingWithSystem()
    {
        var log = new List<ChatMessage> { new(ChatRole.User, "no system message first") };

        Assert.That(() => ChatSession.Restore(log), Throws.ArgumentException);
    }
}
