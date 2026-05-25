using System.Net.Http.Headers;
using System.Text.Json;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Chat;

public sealed record EndpointHealthResult(bool Healthy, string Message);

public sealed record ModelListResult(bool Success, IReadOnlyList<string> Models, string? ErrorMessage);

/// <summary>Server capabilities discovered at connect time by probing
/// non-standard extensions of OpenAI-compatible endpoints.
/// <para><see cref="MaxContextTokens"/> comes from llama.cpp's <c>/props</c>
/// (<c>default_generation_settings.n_ctx</c>) or vLLM's <c>/v1/models</c>
/// (<c>max_model_len</c> on the selected model). Null when neither extension
/// is recognized.</para>
/// <para><see cref="Source"/> identifies which extension answered, useful for
/// UI labelling and diagnostics.</para></summary>
public sealed record EndpointCapabilities(int? MaxContextTokens, string Source)
{
    public static readonly EndpointCapabilities Unknown = new(null, "unknown");
}

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

    /// <summary>Probe non-standard fields that llama.cpp and vLLM bolt onto their
    /// OpenAI-compatible endpoints to discover the configured context size.
    /// Tries <c>/props</c> first (llama-server: <c>default_generation_settings.n_ctx</c>),
    /// then falls back to <c>/v1/models</c> (vLLM: <c>max_model_len</c> on the
    /// matching model entry). Returns <see cref="EndpointCapabilities.Unknown"/>
    /// if nothing is recognized — capability detection must never fail loudly.</summary>
    public async Task<EndpointCapabilities> DetectCapabilitiesAsync(
        EndpointSettings endpoint, CancellationToken cancellationToken = default)
    {
        var fromProps = await TryProbePropsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (fromProps is not null) return fromProps;

        var fromModels = await TryProbeModelsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (fromModels is not null) return fromModels;

        return EndpointCapabilities.Unknown;
    }

    private async Task<EndpointCapabilities?> TryProbePropsAsync(EndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("yamca-llm");
            var propsUri = new Uri(DeriveServerRoot(endpoint.BaseUrl) + "props");
            using var request = new HttpRequestMessage(HttpMethod.Get, propsUri);
            if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var nCtx = TryReadNCtx(doc.RootElement);
            return nCtx is > 0 ? new EndpointCapabilities(nCtx, "llama.cpp /props") : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadNCtx(JsonElement root)
    {
        // Newer llama-server: default_generation_settings.n_ctx
        if (root.TryGetProperty("default_generation_settings", out var dgs) &&
            dgs.ValueKind == JsonValueKind.Object &&
            dgs.TryGetProperty("n_ctx", out var n1) &&
            n1.TryGetInt32(out var v1)) return v1;

        // Some builds put n_ctx at the top level.
        if (root.TryGetProperty("n_ctx", out var n2) && n2.TryGetInt32(out var v2)) return v2;

        return null;
    }

    private async Task<EndpointCapabilities?> TryProbeModelsAsync(EndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("yamca-llm");
            var baseUrl = NormalizeBaseUrl(endpoint.BaseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl + "models"));
            if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            // Prefer the entry whose id matches the configured model; otherwise
            // take the first entry that carries max_model_len at all.
            int? matched = null;
            int? anyMax = null;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("max_model_len", out var mml) || !mml.TryGetInt32(out var len))
                    continue;
                anyMax ??= len;

                if (!string.IsNullOrWhiteSpace(endpoint.Model) &&
                    item.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String &&
                    string.Equals(idEl.GetString(), endpoint.Model, StringComparison.Ordinal))
                {
                    matched = len;
                    break;
                }
            }

            var pick = matched ?? anyMax;
            return pick is > 0 ? new EndpointCapabilities(pick, "vLLM /v1/models max_model_len") : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Endpoint base URL is empty.", nameof(baseUrl));
        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    /// <summary>Returns the scheme + authority of <paramref name="baseUrl"/> with
    /// a trailing slash. llama.cpp's <c>/props</c> lives at the server root, not
    /// under the <c>/v1/</c> path most users configure as their base URL.</summary>
    internal static string DeriveServerRoot(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Endpoint base URL is empty.", nameof(baseUrl));
        var uri = new Uri(baseUrl, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }
}
