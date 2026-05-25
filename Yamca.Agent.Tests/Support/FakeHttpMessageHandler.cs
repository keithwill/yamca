namespace Yamca.Agent.Tests.Support;

/// <summary>Minimal in-process <see cref="HttpMessageHandler"/> that hands every
/// request to a user-supplied delegate. Lets tests verify the bytes sent and
/// return a canned <see cref="HttpResponseMessage"/>.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public static FakeHttpMessageHandler ReturnsOk(string body, string contentType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            RequestBodies.Add(body);
        }
        else
        {
            RequestBodies.Add(string.Empty);
        }
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }
}
