using System.Net;
using System.Text;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;
using Yamca.Agent.Tests.Support;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class EndpointHealthServiceTests
{
    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static EndpointSettings NewEndpoint(string apiKey = "") =>
        new("http://localhost:8080/v1", apiKey, "test-model");

    [Test]
    public async Task ListModels_ReturnsSortedIds()
    {
        var body = """{"object":"list","data":[{"id":"bbb","object":"model"},{"id":"AAA","object":"model"}]}""";
        var handler = FakeHttpMessageHandler.ReturnsOk(body);
        var svc = new EndpointHealthService(new StubFactory(handler));

        var result = await svc.ListModelsAsync(NewEndpoint());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Models, Is.EqualTo(new[] { "AAA", "bbb" }));
        Assert.That(handler.Requests[0].RequestUri!.AbsoluteUri, Is.EqualTo("http://localhost:8080/v1/models"));
    }

    [Test]
    public async Task Check_HappyPath_ReturnsHealthy()
    {
        var body = """{"object":"list","data":[{"id":"only-one","object":"model"}]}""";
        var handler = FakeHttpMessageHandler.ReturnsOk(body);
        var svc = new EndpointHealthService(new StubFactory(handler));

        var result = await svc.CheckAsync(NewEndpoint());

        Assert.That(result.Healthy, Is.True);
        Assert.That(result.Message, Does.Contain("1 model"));
    }

    [Test]
    public async Task Check_404_ReturnsUnhealthy()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
        }));
        var svc = new EndpointHealthService(new StubFactory(handler));

        var result = await svc.CheckAsync(NewEndpoint());

        Assert.That(result.Healthy, Is.False);
        Assert.That(result.Message, Does.Contain("Failed"));
        Assert.That(result.Message, Does.Contain("404"));
    }

    [Test]
    public async Task Check_NetworkException_ReturnsUnhealthy()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var svc = new EndpointHealthService(new StubFactory(handler));

        var result = await svc.CheckAsync(NewEndpoint());

        Assert.That(result.Healthy, Is.False);
        Assert.That(result.Message, Does.Contain("connection refused"));
    }

    [Test]
    public async Task ApiKey_IsSentAsBearerHeader_WhenPresent()
    {
        var body = """{"object":"list","data":[]}""";
        var handler = FakeHttpMessageHandler.ReturnsOk(body);
        var svc = new EndpointHealthService(new StubFactory(handler));

        await svc.ListModelsAsync(NewEndpoint(apiKey: "sk-abc"));

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.That(auth!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(auth.Parameter, Is.EqualTo("sk-abc"));
    }
}
