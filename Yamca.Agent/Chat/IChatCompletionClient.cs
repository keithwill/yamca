namespace Yamca.Agent.Chat;

/// <summary>Streaming chat-completion abstraction. The production implementation
/// posts to an OpenAI-compatible <c>/v1/chat/completions</c> endpoint and parses
/// the SSE stream; tests substitute a scripted fake.</summary>
public interface IChatCompletionClient
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        CancellationToken cancellationToken);
}
