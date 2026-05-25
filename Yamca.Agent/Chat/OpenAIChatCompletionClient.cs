using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yamca.Agent.Chat;

/// <summary>Production <see cref="IChatCompletionClient"/> that POSTs to an
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint and parses the SSE
/// stream by hand. Surfaces <c>delta.reasoning_content</c> (the llama.cpp / vLLM
/// extension) directly as <see cref="LlmReasoningDelta"/>; falls back to
/// <see cref="ReasoningTagStripper"/> for servers that inline <c>&lt;think&gt;</c>
/// tags into the visible content stream.</summary>
public sealed class OpenAIChatCompletionClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly IReadOnlyList<string> _reasoningTags;

    public OpenAIChatCompletionClient(HttpClient http, string model, IReadOnlyList<string>? reasoningTags = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        _model = model;
        _reasoningTags = reasoningTags ?? ReasoningTagStripper.DefaultTags;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildRequest(messages, tools);
        var json = JsonSerializer.Serialize(payload, RequestJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var snippet = body.Length > 500 ? body[..500] + "…" : body;
            throw new HttpRequestException(
                $"Chat completion request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {snippet}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var stripper = new ReasoningTagStripper(_reasoningTags);
        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
        var reasoningOpen = false;
        string? finishReason = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(":", StringComparison.Ordinal)) continue; // SSE comment (keepalive)
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payloadText = line.AsSpan(5).TrimStart().ToString();
            if (payloadText == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payloadText); }
            catch (JsonException) { continue; }

            using (doc)
            {
                // Usage chunks (OpenAI stream_options.include_usage / vLLM /
                // llama-server) arrive as their own SSE frame, either with an
                // empty choices array or none at all. Surface them as their own
                // event so the UI can render real token counts live.
                if (TryReadUsage(doc.RootElement, out var usage))
                {
                    yield return usage;
                }

                if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var reasoningText = TryGetString(delta, "reasoning_content");
                    var contentText = TryGetString(delta, "content");

                    if (!string.IsNullOrEmpty(reasoningText))
                    {
                        reasoning.Append(reasoningText);
                        reasoningOpen = true;
                        yield return new LlmReasoningDelta(reasoningText);
                    }

                    // Transition: reasoning streamed earlier, this chunk has no reasoning but has content.
                    if (reasoningOpen && string.IsNullOrEmpty(reasoningText) && !string.IsNullOrEmpty(contentText))
                    {
                        reasoningOpen = false;
                        yield return LlmReasoningClose.Instance;
                    }

                    if (!string.IsNullOrEmpty(contentText))
                    {
                        // Defense in depth: handle inline <think> tags for servers that don't extract reasoning.
                        var split = stripper.Process(contentText);
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

                    if (delta.TryGetProperty("tool_calls", out var calls) &&
                        calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in calls.EnumerateArray())
                        {
                            var idx = tc.TryGetProperty("index", out var iEl) && iEl.TryGetInt32(out var i) ? i : 0;
                            if (!toolCalls.TryGetValue(idx, out var builder))
                            {
                                builder = new ToolCallBuilder();
                                toolCalls[idx] = builder;
                            }
                            var id = TryGetString(tc, "id");
                            if (!string.IsNullOrEmpty(id)) builder.Id = id;

                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                var name = TryGetString(fn, "name");
                                if (!string.IsNullOrEmpty(name)) builder.Name = name;
                                var args = TryGetString(fn, "arguments");
                                if (!string.IsNullOrEmpty(args)) builder.Arguments.Append(args);
                            }
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString();
                }
            }
        }

        // Drain any partial tag the stream ended on.
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

    private ChatRequestDto BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools)
    {
        var msgDtos = new List<MessageDto>(messages.Count);
        foreach (var m in messages)
        {
            List<ToolCallDto>? tcs = null;
            if (m.ToolCalls is { Count: > 0 })
            {
                tcs = new List<ToolCallDto>(m.ToolCalls.Count);
                foreach (var tc in m.ToolCalls)
                    tcs.Add(new ToolCallDto(tc.Id, "function", new FunctionCallDto(tc.Name, tc.ArgumentsJson)));
            }
            msgDtos.Add(new MessageDto(
                Role: RoleString(m.Role),
                Content: m.Content,
                ToolCallId: m.ToolCallId,
                ToolCalls: tcs));
        }

        List<ToolDto>? toolDtos = null;
        if (tools.Count > 0)
        {
            toolDtos = new List<ToolDto>(tools.Count);
            foreach (var t in tools)
            {
                using var schema = JsonDocument.Parse(t.ParametersJsonSchema);
                toolDtos.Add(new ToolDto("function",
                    new FunctionDefDto(t.Name, t.Description, schema.RootElement.Clone())));
            }
        }

        return new ChatRequestDto(
            _model,
            Stream: true,
            msgDtos,
            toolDtos,
            StreamOptions: new StreamOptionsDto(IncludeUsage: true));
    }

    private static bool TryReadUsage(JsonElement root, out LlmUsageUpdate update)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var prompt = TryGetInt(usage, "prompt_tokens") ?? 0;
            var completion = TryGetInt(usage, "completion_tokens") ?? 0;

            int? cached = null;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                cached = TryGetInt(details, "cached_tokens");
            }

            // llama-server attaches a sibling `timings` block with prompt-cache hits.
            if (cached is null && root.TryGetProperty("timings", out var timings) &&
                timings.ValueKind == JsonValueKind.Object)
            {
                cached = TryGetInt(timings, "cache_n");
            }

            if (prompt > 0 || completion > 0)
            {
                update = new LlmUsageUpdate(prompt, completion, cached);
                return true;
            }
        }

        update = default!;
        return false;
    }

    private static int? TryGetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class ToolCallBuilder
    {
        public string? Id;
        public string? Name;
        public StringBuilder Arguments { get; } = new();
    }

    private sealed record ChatRequestDto(
        string Model,
        bool Stream,
        IReadOnlyList<MessageDto> Messages,
        IReadOnlyList<ToolDto>? Tools,
        StreamOptionsDto? StreamOptions);

    private sealed record StreamOptionsDto(bool IncludeUsage);

    private sealed record MessageDto(
        string Role,
        string? Content,
        string? ToolCallId,
        IReadOnlyList<ToolCallDto>? ToolCalls);

    private sealed record ToolCallDto(string Id, string Type, FunctionCallDto Function);

    private sealed record FunctionCallDto(string Name, string Arguments);

    private sealed record ToolDto(string Type, FunctionDefDto Function);

    private sealed record FunctionDefDto(string Name, string Description, JsonElement Parameters);
}
