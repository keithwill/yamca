using System.Text;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Chat;

/// <summary>Ordered message log for one chat conversation. All system-role content
/// (user-authored system prompt, workspace context, instruction-file bodies) is
/// concatenated into a single system message at index 0, separated by blank lines.
/// This is for maximal compatibility with OpenAI-compatible servers whose chat
/// templates only honor the first <c>system</c> role.</summary>
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

    // Restore path: adopt a previously captured message log verbatim (including an
    // already-compacted system message at index 0).
    private ChatSession(List<ChatMessage> messages)
    {
        SystemPrompt = messages[0].Content;
        _messages = messages;
        RecomputeEstimatedChars();
    }

    /// <summary>Rebuild a session from a persisted message log, preserving it exactly so
    /// the model sees the same context it had when the chat was saved (including any
    /// compaction summary folded into the system message). The first message must be the
    /// system message.</summary>
    public static ChatSession Restore(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
            throw new ArgumentException("Restored message log must begin with a system message.", nameof(messages));
        return new ChatSession(messages.ToList());
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
        _messages = new List<ChatMessage> { new ChatMessage(ChatRole.System, systemContent) };
        _estimatedChars = systemContent.Length;
    }

    public string SystemPrompt { get; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Rough char/4 estimate of the input tokens the next LLM call will see.
    /// Updated synchronously as messages are appended.</summary>
    public int EstimatedInputTokens => (_estimatedChars + 3) / 4;

    /// <summary>Flat char-equivalent allowance added to the token estimate per attached
    /// image. Real image token cost is computed server-side from resolution/tiles; this is
    /// only a coarse nudge so the auto-compaction heuristic isn't blind to image payloads.</summary>
    private const int ImageCharEstimate = 1000;

    public void AppendUser(string content, IReadOnlyList<ChatImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new ChatMessage(ChatRole.User, content, Images: images));
        _estimatedChars += content.Length;
        if (images is not null)
            _estimatedChars += images.Count * ImageCharEstimate;
    }

    public void AppendAssistant(string content, IReadOnlyList<LlmToolCallRequest> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCalls);

        IReadOnlyList<ChatToolCall>? calls = null;
        if (toolCalls.Count > 0)
        {
            var list = new List<ChatToolCall>(toolCalls.Count);
            foreach (var tc in toolCalls)
                list.Add(new ChatToolCall(tc.CallId, tc.ToolName, tc.ArgumentsJson));
            calls = list;
        }

        _messages.Add(new ChatMessage(ChatRole.Assistant, content, ToolCalls: calls));
        _estimatedChars += content.Length;
        foreach (var tc in toolCalls)
            _estimatedChars += tc.ToolName.Length + tc.ArgumentsJson.Length;
    }

    public void AppendToolResult(string toolCallId, string content)
    {
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(new ChatMessage(ChatRole.Tool, content, ToolCallId: toolCallId));
        _estimatedChars += content.Length;
    }

    private const string SummaryMarker = "\n\n[Summary of earlier conversation]: ";

    /// <summary>Replace messages in the range [1, <paramref name="keepFromMessageIndex"/>)
    /// with a model-generated summary appended to the system message. The system
    /// message at index 0 grows to include (or have replaced) a marker section
    /// containing <paramref name="summary"/>. Idempotent across re-compactions:
    /// a prior summary block is stripped before the new one is appended so they
    /// don't stack.</summary>
    public void Compact(string summary, int keepFromMessageIndex)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (keepFromMessageIndex < 1 || keepFromMessageIndex > _messages.Count)
            throw new ArgumentOutOfRangeException(nameof(keepFromMessageIndex));

        var originalSystem = _messages[0].Content;
        var markerIdx = originalSystem.IndexOf(SummaryMarker, StringComparison.Ordinal);
        var baseSystem = markerIdx >= 0 ? originalSystem[..markerIdx] : originalSystem;

        var newSystemContent = baseSystem + SummaryMarker + summary;
        _messages[0] = new ChatMessage(ChatRole.System, newSystemContent);

        if (keepFromMessageIndex > 1)
            _messages.RemoveRange(1, keepFromMessageIndex - 1);

        RecomputeEstimatedChars();
    }

    /// <summary>Walks backward through the message log looking for the Nth-most-recent
    /// user message and returns its index — that is the cutoff above which earlier
    /// messages will be summarized. Returns <c>-1</c> when there is nothing to
    /// summarize: either fewer than <paramref name="keepTurns"/> user turns exist,
    /// or all existing user turns fall within the keep window (so the cutoff
    /// would be the very first user message at index 1, with nothing earlier).
    /// The result, when positive, is always <c>&gt;= 2</c>.</summary>
    public int FindKeepFromIndexForRecentTurns(int keepTurns)
    {
        if (keepTurns < 1) keepTurns = 1;
        var found = 0;
        for (var i = _messages.Count - 1; i >= 1; i--)
        {
            if (_messages[i].Role != ChatRole.User) continue;
            found++;
            if (found == keepTurns)
                return i >= 2 ? i : -1;
        }
        return -1;
    }

    private void RecomputeEstimatedChars()
    {
        var total = 0;
        foreach (var m in _messages)
        {
            total += m.Content.Length;
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                    total += tc.Name.Length + tc.ArgumentsJson.Length;
            }
            if (m.Images is { Count: > 0 })
                total += m.Images.Count * ImageCharEstimate;
        }
        _estimatedChars = total;
    }
}
