using System.Net.Http;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class EndpointClientFactoryTests
{
    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static EndpointSettings Endpoint(string baseUrl = "http://localhost:8080/v1", string apiKey = "", string model = "m") =>
        new(Guid.NewGuid(), Name: null, baseUrl, apiKey, model);

    [Test]
    public void CreateHttpClient_EnforcesTrailingSlashOnBaseAddress()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        using var http = factory.CreateHttpClient(Endpoint("http://localhost:8080/v1"));

        Assert.That(http.BaseAddress, Is.EqualTo(new Uri("http://localhost:8080/v1/")));
    }

    [Test]
    public void CreateHttpClient_KeepsExistingTrailingSlash()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        using var http = factory.CreateHttpClient(Endpoint("http://localhost:8080/v1/"));

        Assert.That(http.BaseAddress, Is.EqualTo(new Uri("http://localhost:8080/v1/")));
    }

    [Test]
    public void CreateHttpClient_SetsBearerAuth_WhenApiKeyPresent()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        using var http = factory.CreateHttpClient(Endpoint(apiKey: "secret"));

        Assert.That(http.DefaultRequestHeaders.Authorization?.Scheme, Is.EqualTo("Bearer"));
        Assert.That(http.DefaultRequestHeaders.Authorization?.Parameter, Is.EqualTo("secret"));
    }

    [Test]
    public void CreateHttpClient_LeavesAuthUnset_WhenApiKeyBlank()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        using var http = factory.CreateHttpClient(Endpoint(apiKey: "  "));

        Assert.That(http.DefaultRequestHeaders.Authorization, Is.Null);
    }

    [Test]
    public void CreateHttpClient_DisablesTimeout_SoStreamingTurnsAreUnbounded()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        using var http = factory.CreateHttpClient(Endpoint());

        Assert.That(http.Timeout, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void CreateCompletionClient_ReturnsConfiguredClient()
    {
        var factory = new EndpointClientFactory(new StubFactory());

        var client = factory.CreateCompletionClient(Endpoint(model: ""));

        // Blank model falls back internally; the contract here is simply that a usable
        // client is produced (the OpenAI wire client used by the loop, compactor, subagents).
        Assert.That(client, Is.InstanceOf<OpenAIChatCompletionClient>());
    }
}
