using OpenAI.Chat;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Ordered message log for one chat conversation. The user's system prompt
/// sits at index 0 unchanged across sessions (so prompt-caching can reuse it), and
/// any per-session context (e.g. workspace path) is appended as a second system
/// message at index 1.</summary>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages;

    public ChatSession(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        SystemPrompt = systemPrompt;
        _messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
    }

    public ChatSession(IWorkspace workspace, string systemPrompt)
        : this(systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _messages.Add(new SystemChatMessage($"Current workspace: {workspace.RootPath}"));
    }

    public string SystemPrompt { get; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AppendUser(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new UserChatMessage(content));
    }

    public void AppendAssistant(string content, IReadOnlyList<LlmToolCallRequest> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCalls);

        AssistantChatMessage msg;
        if (toolCalls.Count == 0)
        {
            msg = new AssistantChatMessage(content);
        }
        else
        {
            var calls = toolCalls.Select(tc => ChatToolCall.CreateFunctionToolCall(
                tc.CallId, tc.ToolName, BinaryData.FromString(tc.ArgumentsJson))).ToList();
            msg = new AssistantChatMessage(calls);
            if (!string.IsNullOrEmpty(content))
                msg.Content.Add(ChatMessageContentPart.CreateTextPart(content));
        }
        _messages.Add(msg);
    }

    public void AppendToolResult(string toolCallId, string content)
    {
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new ToolChatMessage(toolCallId, content));
    }
}
