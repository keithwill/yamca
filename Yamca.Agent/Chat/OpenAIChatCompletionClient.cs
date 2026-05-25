using System.Runtime.CompilerServices;
using System.Text;
using OpenAI.Chat;

namespace Yamca.Agent.Chat;

/// <summary>Production <see cref="IChatCompletionClient"/> backed by the official
/// <see cref="OpenAI.Chat.ChatClient"/>. Aggregates the SDK's fragmented
/// <see cref="StreamingChatCompletionUpdate"/> chunks into our simpler event stream
/// and splits inline reasoning tags (<c>&lt;think&gt;</c>, <c>&lt;thinking&gt;</c>,
/// <c>&lt;reasoning&gt;</c>) out of the visible content stream.</summary>
public sealed class OpenAIChatCompletionClient : IChatCompletionClient
{
    private readonly ChatClient _client;
    private readonly IReadOnlyList<string> _reasoningTags;

    public OpenAIChatCompletionClient(ChatClient client, IReadOnlyList<string>? reasoningTags = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _reasoningTags = reasoningTags ?? ReasoningTagStripper.DefaultTags;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new ChatCompletionOptions();
        foreach (var tool in tools) options.Tools.Add(tool);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var stripper = new ReasoningTagStripper(_reasoningTags);
        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
        string? finishReason = null;

        var stream = _client.CompleteChatStreamingAsync(messages, options, cancellationToken);
        await foreach (var update in stream.ConfigureAwait(false))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (string.IsNullOrEmpty(part.Text)) continue;
                var split = stripper.Process(part.Text);
                if (split.Reasoning.Length > 0)
                {
                    reasoning.Append(split.Reasoning);
                    yield return new LlmReasoningDelta(split.Reasoning);
                }
                if (split.Visible.Length > 0)
                {
                    content.Append(split.Visible);
                    yield return new LlmContentDelta(split.Visible);
                }
                if (split.JustClosed)
                {
                    yield return LlmReasoningClose.Instance;
                }
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

        // Drain any partial tag the stream ended on (e.g. a model that opened
        // <think> but never closed it). Pushed to whichever sink the stripper
        // is currently in.
        var tail = stripper.Flush();
        if (tail.Reasoning.Length > 0)
        {
            reasoning.Append(tail.Reasoning);
            yield return new LlmReasoningDelta(tail.Reasoning);
        }
        if (tail.Visible.Length > 0)
        {
            content.Append(tail.Visible);
            yield return new LlmContentDelta(tail.Visible);
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

        yield return new LlmAssistantTurnComplete(
            content.ToString(), completed, finishReason, reasoning.ToString());
    }

    private sealed class ToolCallBuilder
    {
        public string? Id;
        public string? Name;
        public StringBuilder Arguments { get; } = new();
    }
}
