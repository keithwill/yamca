using System.Text;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;

namespace Yamca.Web.Services;

/// <summary>Produces a plain-text summary of a slice of chat history by calling
/// the locked endpoint with a one-off summarization prompt. The result is meant
/// to be inlined into <see cref="ChatSession.Compact(string, int)"/> as the
/// replacement for the discarded messages.</summary>
public sealed class ContextCompactor
{
    private const string SummarizationSystemPrompt =
        "You are summarizing a software-engineering chat between a user and a coding-agent assistant. " +
        "Produce a concise plain-text summary (no Markdown) of about 200-400 words that preserves: " +
        "file paths mentioned, function/class/symbol names, decisions and plans made, " +
        "tool invocations and their outcomes, open questions, and the user's overall intent. " +
        "Drop social pleasantries and verbatim code blocks. Write in past tense. " +
        "Output the summary text only — no headers, no preamble.";

    private readonly EndpointClientFactory _clientFactory;

    public ContextCompactor(EndpointClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _clientFactory = clientFactory;
    }

    /// <summary>Summarize <c>session.Messages[1 .. keepFromMessageIndex)</c>. The
    /// system message at index 0 and messages from <paramref name="keepFromMessageIndex"/>
    /// onward are not included in the summarization request.</summary>
    public async Task<string> SummarizeAsync(
        ChatSession session,
        EndpointSettings endpoint,
        int keepFromMessageIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (keepFromMessageIndex < 1 || keepFromMessageIndex > session.Messages.Count)
            throw new ArgumentOutOfRangeException(nameof(keepFromMessageIndex));

        var transcript = BuildTranscript(session.Messages, keepFromMessageIndex);
        if (transcript.Length == 0) return string.Empty;

        var client = _clientFactory.CreateCompletionClient(endpoint);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SummarizationSystemPrompt),
            new(ChatRole.User, "Summarize the following conversation:\n\n" + transcript),
        };

        var sb = new StringBuilder();
        await foreach (var ev in client.StreamAsync(messages, Array.Empty<ChatTool>(), cancellationToken)
                                       .ConfigureAwait(false))
        {
            if (ev is LlmContentDelta delta)
                sb.Append(delta.Text);
        }
        return sb.ToString().Trim();
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages, int keepFromMessageIndex)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < keepFromMessageIndex; i++)
        {
            var m = messages[i];
            var label = m.Role switch
            {
                ChatRole.User => "USER",
                ChatRole.Assistant => "ASSISTANT",
                ChatRole.Tool => "TOOL_RESULT",
                _ => m.Role.ToString().ToUpperInvariant(),
            };
            sb.Append(label).Append(": ");
            if (!string.IsNullOrEmpty(m.Content))
                sb.AppendLine(m.Content);
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                {
                    sb.Append("  (tool call: ").Append(tc.Name).Append(' ').AppendLine(tc.ArgumentsJson);
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
