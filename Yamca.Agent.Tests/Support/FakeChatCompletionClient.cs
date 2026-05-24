using OpenAI.Chat;
using Yamca.Agent.Chat;

namespace Yamca.Agent.Tests.Support;

/// <summary>Scripted <see cref="IChatCompletionClient"/>. Each enqueued response
/// represents one assistant turn — yielded in order across successive
/// <see cref="StreamAsync"/> calls.</summary>
internal sealed class FakeChatCompletionClient : IChatCompletionClient
{
    private readonly Queue<ScriptedResponse> _responses = new();
    private readonly List<RecordedCall> _calls = new();

    public IReadOnlyList<RecordedCall> Calls => _calls;
    public int PendingResponses => _responses.Count;

    public FakeChatCompletionClient EnqueueText(string content) =>
        Enqueue(new ScriptedResponse(content, Array.Empty<LlmToolCallRequest>(), "stop"));

    public FakeChatCompletionClient EnqueueToolCall(string callId, string toolName, string argumentsJson) =>
        Enqueue(new ScriptedResponse(
            "",
            new[] { new LlmToolCallRequest(callId, toolName, argumentsJson) },
            "tool_calls"));

    public FakeChatCompletionClient Enqueue(ScriptedResponse response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _calls.Add(new RecordedCall(messages.ToList(), tools.ToList()));

        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeChatCompletionClient ran out of scripted responses.");

        var response = _responses.Dequeue();

        foreach (var chunk in response.ContentChunks ?? new[] { response.Content })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(chunk))
                yield return new LlmContentDelta(chunk);
            await Task.Yield();
        }

        yield return new LlmAssistantTurnComplete(response.Content, response.ToolCalls, response.FinishReason);
    }

    internal sealed record RecordedCall(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatTool> Tools);
}

internal sealed record ScriptedResponse(
    string Content,
    IReadOnlyList<LlmToolCallRequest> ToolCalls,
    string? FinishReason,
    IReadOnlyList<string>? ContentChunks = null);
