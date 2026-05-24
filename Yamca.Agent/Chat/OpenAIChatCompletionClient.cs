using System.Runtime.CompilerServices;
using System.Text;
using OpenAI.Chat;

namespace Yamca.Agent.Chat;

/// <summary>Production <see cref="IChatCompletionClient"/> backed by the official
/// <see cref="OpenAI.Chat.ChatClient"/>. Aggregates the SDK's fragmented
/// <see cref="StreamingChatCompletionUpdate"/> chunks into our simpler event stream.</summary>
public sealed class OpenAIChatCompletionClient : IChatCompletionClient
{
    private readonly ChatClient _client;

    public OpenAIChatCompletionClient(ChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
        string? finishReason = null;

        var stream = _client.CompleteChatStreamingAsync(messages, options, cancellationToken);
        await foreach (var update in stream.ConfigureAwait(false))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (string.IsNullOrEmpty(part.Text)) continue;
                content.Append(part.Text);
                yield return new LlmContentDelta(part.Text);
            }

            foreach (var tcu in update.ToolCallUpdates)
            {
                if (!toolCalls.TryGetValue(tcu.Index, out var builder))
                {
                    builder = new ToolCallBuilder();
                    toolCalls[tcu.Index] = builder;
                }
                if (!string.IsNullOrEmpty(tcu.ToolCallId)) builder.Id = tcu.ToolCallId;
                if (!string.IsNullOrEmpty(tcu.FunctionName)) builder.Name = tcu.FunctionName;
                if (tcu.FunctionArgumentsUpdate is { } argsDelta)
                {
                    var s = argsDelta.ToString();
                    if (!string.IsNullOrEmpty(s)) builder.Arguments.Append(s);
                }
            }

            if (update.FinishReason is { } reason) finishReason = reason.ToString();
        }

        var completed = new List<LlmToolCallRequest>(toolCalls.Count);
        foreach (var (_, b) in toolCalls)
        {
            if (b.Id is null || b.Name is null) continue;
            completed.Add(new LlmToolCallRequest(
                CallId: b.Id,
                ToolName: b.Name,
                ArgumentsJson: b.Arguments.Length == 0 ? "{}" : b.Arguments.ToString()));
        }

        yield return new LlmAssistantTurnComplete(content.ToString(), completed, finishReason);
    }

    private sealed class ToolCallBuilder
    {
        public string? Id;
        public string? Name;
        public StringBuilder Arguments { get; } = new();
    }
}
