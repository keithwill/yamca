using System.Net.Http.Headers;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Chat;

/// <summary>Builds a configured <see cref="IChatCompletionClient"/> for an
/// <see cref="EndpointSettings"/>: a "yamca-llm" named <see cref="HttpClient"/> pointed at the
/// endpoint's base URL, with bearer auth and no request timeout (streaming turns are unbounded),
/// wrapped in an <see cref="OpenAIChatCompletionClient"/>. Centralizes the construction the chat
/// loop, subagent runner, and context compactor would otherwise each duplicate, so endpoint and
/// auth behavior can't drift between them.</summary>
public sealed class EndpointClientFactory
{
    // Sent when the endpoint leaves the model blank. Single-model OpenAI-compatible servers
    // (the common local case) ignore the field, but the wire format needs a non-empty value.
    private const string FallbackModelId = "local-model";

    private readonly IHttpClientFactory _httpFactory;

    public EndpointClientFactory(IHttpClientFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        _httpFactory = httpFactory;
    }

    /// <summary>Configured chat-completion client for <paramref name="endpoint"/>, using the
    /// endpoint's model id (or <c>"local-model"</c> when blank).</summary>
    public IChatCompletionClient CreateCompletionClient(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var modelId = string.IsNullOrWhiteSpace(endpoint.Model) ? FallbackModelId : endpoint.Model;
        return new OpenAIChatCompletionClient(CreateHttpClient(endpoint), modelId);
    }

    /// <summary>The underlying "yamca-llm" <see cref="HttpClient"/> configured for
    /// <paramref name="endpoint"/>: base URL with an enforced trailing slash (so the client's
    /// relative request URIs resolve under it), bearer auth when an API key is set, and an
    /// infinite timeout so a long streaming turn is never cut off by the handler.</summary>
    public HttpClient CreateHttpClient(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var baseUrl = endpoint.BaseUrl.EndsWith('/') ? endpoint.BaseUrl : endpoint.BaseUrl + "/";
        var http = _httpFactory.CreateClient("yamca-llm");
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(endpoint.ApiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }
}
