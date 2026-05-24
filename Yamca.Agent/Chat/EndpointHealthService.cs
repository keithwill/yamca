using OpenAI;
using System.ClientModel;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Chat;

public sealed record EndpointHealthResult(bool Healthy, string Message);

public sealed class EndpointHealthService
{
    public async Task<EndpointHealthResult> CheckAsync(EndpointSettings endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
            var client = new OpenAIClient(new ApiKeyCredential(endpoint.ApiKey ?? string.Empty), options);
            var modelClient = client.GetOpenAIModelClient();

            var response = await modelClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var count = response.Value.Count;
            return new EndpointHealthResult(true, $"OK — {count} model{(count == 1 ? "" : "s")} available at {endpoint.BaseUrl}");
        }
        catch (Exception ex)
        {
            return new EndpointHealthResult(false, $"Failed: {ex.Message}");
        }
    }
}
