using System.Text;
using OpenAI.Chat;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Ordered message log for one chat conversation. All system-role content
/// (user-authored system prompt, workspace context, instruction-file bodies) is
/// concatenated into a single <see cref="SystemChatMessage"/> at index 0, separated
/// by blank lines. This is for maximal compatibility with OpenAI-compatible servers
/// whose chat templates only honor the first <c>system</c> role.</summary>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages;

    public ChatSession(string systemPrompt)
        : this(systemPrompt, workspace: null, instructionMessages: null)
    {
    }

    public ChatSession(IWorkspace workspace, string systemPrompt)
        : this(systemPrompt, workspace, instructionMessages: null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
    }

    public ChatSession(IWorkspace workspace, string systemPrompt, IReadOnlyList<string> instructionMessages)
        : this(systemPrompt, workspace, instructionMessages)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(instructionMessages);
    }

    private ChatSession(string systemPrompt, IWorkspace? workspace, IReadOnlyList<string>? instructionMessages)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        SystemPrompt = systemPrompt;

        var sb = new StringBuilder(systemPrompt);
        if (workspace is not null)
            sb.Append("\n\nCurrent workspace: ").Append(workspace.RootPath);

        if (instructionMessages is not null)
        {
            foreach (var instruction in instructionMessages)
            {
                if (string.IsNullOrWhiteSpace(instruction)) continue;
                sb.Append("\n\n").Append(instruction);
            }
        }

        _messages = new List<ChatMessage> { new SystemChatMessage(sb.ToString()) };
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
