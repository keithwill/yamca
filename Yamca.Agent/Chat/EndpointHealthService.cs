using OpenAI;
using System.ClientModel;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Chat;

public sealed record EndpointHealthResult(bool Healthy, string Message);

public sealed record ModelListResult(bool Success, IReadOnlyList<string> Models, string? ErrorMessage);

public sealed class EndpointHealthService
{
    public async Task<EndpointHealthResult> CheckAsync(EndpointSettings endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var modelClient = CreateClient(endpoint).GetOpenAIModelClient();
            var response = await modelClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var count = response.Value.Count;
            return new EndpointHealthResult(true, $"OK — {count} model{(count == 1 ? "" : "s")} available at {endpoint.BaseUrl}");
        }
        catch (Exception ex)
        {
            return new EndpointHealthResult(false, $"Failed: {ex.Message}");
        }
    }

    public async Task<ModelListResult> ListModelsAsync(EndpointSettings endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var modelClient = CreateClient(endpoint).GetOpenAIModelClient();
            var response = await modelClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var ids = response.Value.Select(m => m.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            return new ModelListResult(true, ids, null);
        }
        catch (Exception ex)
        {
            return new ModelListResult(false, Array.Empty<string>(), ex.Message);
        }
    }

    private static OpenAIClient CreateClient(EndpointSettings endpoint)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
        return new OpenAIClient(new ApiKeyCredential(endpoint.ApiKey ?? string.Empty), options);
    }
}
