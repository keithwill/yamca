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
    private int _estimatedChars;

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

        var systemContent = sb.ToString();
        _messages = new List<ChatMessage> { new SystemChatMessage(systemContent) };
        _estimatedChars = systemContent.Length;
    }

    public string SystemPrompt { get; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Rough char/4 estimate of the input tokens the next LLM call will see.
    /// Updated synchronously as messages are appended. The OpenAI .NET SDK 2.10 does
    /// not yet expose <c>stream_options.include_usage</c> publicly (tracked in
    /// openai-dotnet#616), so we estimate rather than depend on internals — swap to
    /// server-reported usage when that property goes public.</summary>
    public int EstimatedInputTokens => (_estimatedChars + 3) / 4;

    public void AppendUser(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new UserChatMessage(content));
        _estimatedChars += content.Length;
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
        _estimatedChars += content.Length;
        foreach (var tc in toolCalls)
            _estimatedChars += tc.ToolName.Length + tc.ArgumentsJson.Length;
    }

    public void AppendToolResult(string toolCallId, string content)
    {
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new ToolChatMessage(toolCallId, content));
        _estimatedChars += content.Length;
    }
}
