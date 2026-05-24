using OpenAI.Chat;

namespace Yamca.Agent.Chat;

/// <summary>Thin abstraction over <see cref="OpenAI.Chat.ChatClient"/>'s streaming API.
/// Lets the agent loop be unit-tested with a scripted fake instead of a live endpoint.</summary>
public interface IChatCompletionClient
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        CancellationToken cancellationToken);
}
