using OpenAI.Chat;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Ordered message log for one chat conversation. Holds the
/// <see cref="OpenAI.Chat.ChatMessage"/>s in the exact order the LLM expects them,
/// with the system prompt always at index 0.</summary>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages;

    public ChatSession(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        SystemPrompt = systemPrompt;
        _messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
    }

    public ChatSession(IWorkspace workspace, string systemPromptTemplate)
        : this(RenderSystemPrompt(systemPromptTemplate, workspace))
    {
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

    private static string RenderSystemPrompt(string template, IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(workspace);
        return template.Replace("{{workspace}}", workspace.RootPath, StringComparison.Ordinal);
    }
}
