using System.Net.Http.Headers;
using System.Text.Json;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Chat;

public sealed record EndpointHealthResult(bool Healthy, string Message);

public sealed record ModelListResult(bool Success, IReadOnlyList<string> Models, string? ErrorMessage);

public sealed class EndpointHealthService
{
    private readonly IHttpClientFactory _httpFactory;

    public EndpointHealthService(IHttpClientFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        _httpFactory = httpFactory;
    }

    public async Task<EndpointHealthResult> CheckAsync(EndpointSettings endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = await GetModelIdsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var count = ids.Count;
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
            var ids = await GetModelIdsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var sorted = ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            return new ModelListResult(true, sorted, null);
        }
        catch (Exception ex)
        {
            return new ModelListResult(false, Array.Empty<string>(), ex.Message);
        }
    }

    private async Task<IReadOnlyList<string>> GetModelIdsAsync(EndpointSettings endpoint, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("yamca-llm");
        var baseUrl = NormalizeBaseUrl(endpoint.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl + "models"));
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = body.Length > 200 ? body[..200] + "…" : body;
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. {snippet}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    internal static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Endpoint base URL is empty.", nameof(baseUrl));
        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }
}
