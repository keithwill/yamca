using System.Net;
using System.Text;
using System.Text.Json;
using Yamca.Agent.Chat;
using Yamca.Agent.Tests.Support;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class OpenAIChatCompletionClientTests
{
    private static HttpClient ClientFor(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:8080/v1/") };

    private static HttpResponseMessage SseResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static string Sse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines)
            sb.Append("data: ").Append(line).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static async Task<List<LlmStreamEvent>> Collect(IAsyncEnumerable<LlmStreamEvent> stream)
    {
        var list = new List<LlmStreamEvent>();
        await foreach (var ev in stream) list.Add(ev);
        return list;
    }

    private static readonly IReadOnlyList<ChatMessage> Msgs = new[]
    {
        new ChatMessage(ChatRole.System, "be helpful"),
        new ChatMessage(ChatRole.User, "hi"),
    };

    [Test]
    public async Task TextOnly_ThreeChunks_ProducesThreeContentDeltasAndComplete()
    {
        var sse = Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"lo, "},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"world"},"finish_reason":"stop"}]}""");

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse)));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var events = await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        var deltas = events.OfType<LlmContentDelta>().Select(d => d.Text).ToArray();
        Assert.That(deltas, Is.EqualTo(new[] { "Hel", "lo, ", "world" }));

        var done = events.OfType<LlmAssistantTurnComplete>().Single();
        Assert.That(done.Content, Is.EqualTo("Hello, world"));
        Assert.That(done.FinishReason, Is.EqualTo("stop"));
        Assert.That(done.ToolCalls, Is.Empty);
    }

    [Test]
    public async Task ReasoningContent_ThenContent_EmitsReasoningCloseOnTransition()
    {
        var sse = Sse(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning_content":" more"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":"stop"}]}""");

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse)));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var events = await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        var rdeltas = events.OfType<LlmReasoningDelta>().Select(r => r.Text).ToArray();
        Assert.That(rdeltas, Is.EqualTo(new[] { "thinking", " more" }));
        Assert.That(events.OfType<LlmReasoningClose>().Count(), Is.EqualTo(1));
        Assert.That(events.OfType<LlmContentDelta>().Single().Text, Is.EqualTo("answer"));

        var done = events.OfType<LlmAssistantTurnComplete>().Single();
        Assert.That(done.Content, Is.EqualTo("answer"));
        Assert.That(done.Reasoning, Is.EqualTo("thinking more"));
    }

    [Test]
    public async Task InlineThinkTag_StripperFallback_RoutesReasoning()
    {
        var sse = Sse(
            """{"choices":[{"index":0,"delta":{"content":"<think>thoughts</think>final"},"finish_reason":"stop"}]}""");

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse)));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var events = await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        var done = events.OfType<LlmAssistantTurnComplete>().Single();
        Assert.That(done.Content, Is.EqualTo("final"));
        Assert.That(done.Reasoning, Is.EqualTo("thoughts"));
        Assert.That(events.OfType<LlmReasoningClose>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task ToolCall_StreamedInFragments_AggregatesArguments()
    {
        var sse = Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"pa"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"a.txt"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"}"}}]},"finish_reason":"tool_calls"}]}""");

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse)));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var events = await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        var done = events.OfType<LlmAssistantTurnComplete>().Single();
        Assert.That(done.ToolCalls, Has.Count.EqualTo(1));
        Assert.That(done.ToolCalls[0].CallId, Is.EqualTo("call_1"));
        Assert.That(done.ToolCalls[0].ToolName, Is.EqualTo("read_file"));
        Assert.That(done.ToolCalls[0].ArgumentsJson, Is.EqualTo("""{"path":"a.txt"}"""));
        Assert.That(done.FinishReason, Is.EqualTo("tool_calls"));
    }

    [Test]
    public void Non2xx_ThrowsWithBodySnippet()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("model not found", Encoding.UTF8, "text/plain"),
        }));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
            await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None)));

        Assert.That(ex!.Message, Does.Contain("400"));
        Assert.That(ex.Message, Does.Contain("model not found"));
    }

    [Test]
    public void CancellationMidStream_Propagates()
    {
        var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SseResponse(""));
        });
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var ex = Assert.CatchAsync(async () =>
            await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), cts.Token)));
        Assert.That(ex, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Request_HasBearerAuthHeader_WhenSetOnHttpClient()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(Sse(
            """{"choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":"stop"}]}"""))));
        var http = ClientFor(handler);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-test");
        var client = new OpenAIChatCompletionClient(http, "test-model");

        await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(handler.Requests[0].Headers.Authorization!.Parameter, Is.EqualTo("sk-test"));
        Assert.That(handler.Requests[0].RequestUri!.AbsolutePath, Is.EqualTo("/v1/chat/completions"));
    }

    [Test]
    public async Task RequestBody_SerializesMessagesAndToolsInOpenAIShape()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(Sse(
            """{"choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":"stop"}]}"""))));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "my-model");

        var msgs = new[]
        {
            new ChatMessage(ChatRole.System, "sys"),
            new ChatMessage(ChatRole.User, "ask"),
            new ChatMessage(ChatRole.Assistant, "", ToolCalls: new[]
            {
                new ChatToolCall("call_1", "read_file", """{"path":"a"}"""),
            }),
            new ChatMessage(ChatRole.Tool, "result", ToolCallId: "call_1"),
        };
        var tools = new[]
        {
            new ChatTool("read_file", "Read a file", """{"type":"object","properties":{"path":{"type":"string"}}}"""),
        };

        await Collect(client.StreamAsync(msgs, tools, CancellationToken.None));

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(1));
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("model").GetString(), Is.EqualTo("my-model"));
        Assert.That(root.GetProperty("stream").GetBoolean(), Is.True);

        var jsonMsgs = root.GetProperty("messages");
        Assert.That(jsonMsgs.GetArrayLength(), Is.EqualTo(4));
        Assert.That(jsonMsgs[0].GetProperty("role").GetString(), Is.EqualTo("system"));
        Assert.That(jsonMsgs[2].GetProperty("role").GetString(), Is.EqualTo("assistant"));

        var asstToolCalls = jsonMsgs[2].GetProperty("tool_calls");
        Assert.That(asstToolCalls.GetArrayLength(), Is.EqualTo(1));
        Assert.That(asstToolCalls[0].GetProperty("id").GetString(), Is.EqualTo("call_1"));
        Assert.That(asstToolCalls[0].GetProperty("type").GetString(), Is.EqualTo("function"));
        Assert.That(asstToolCalls[0].GetProperty("function").GetProperty("name").GetString(), Is.EqualTo("read_file"));
        Assert.That(asstToolCalls[0].GetProperty("function").GetProperty("arguments").GetString(), Is.EqualTo("""{"path":"a"}"""));

        Assert.That(jsonMsgs[3].GetProperty("role").GetString(), Is.EqualTo("tool"));
        Assert.That(jsonMsgs[3].GetProperty("tool_call_id").GetString(), Is.EqualTo("call_1"));

        var jsonTools = root.GetProperty("tools");
        Assert.That(jsonTools.GetArrayLength(), Is.EqualTo(1));
        Assert.That(jsonTools[0].GetProperty("type").GetString(), Is.EqualTo("function"));
        Assert.That(jsonTools[0].GetProperty("function").GetProperty("name").GetString(), Is.EqualTo("read_file"));
        Assert.That(jsonTools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString(), Is.EqualTo("object"));
    }

    [Test]
    public async Task SseCommentLines_AreSkipped()
    {
        // Many local servers emit `: keepalive\n` lines while idle.
        var body = ": keepalive\n\n" + Sse(
            """{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}""");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SseResponse(body)));
        var client = new OpenAIChatCompletionClient(ClientFor(handler), "test-model");

        var events = await Collect(client.StreamAsync(Msgs, Array.Empty<ChatTool>(), CancellationToken.None));

        Assert.That(events.OfType<LlmContentDelta>().Single().Text, Is.EqualTo("ok"));
    }
}
