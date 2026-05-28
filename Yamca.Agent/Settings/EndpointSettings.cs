namespace Yamca.Agent.Settings;

/// <summary>
/// OpenAI-compatible endpoint configuration. The agent is local-LLM first, so
/// <see cref="BaseUrl"/> typically points at a llama.cpp or vllm server.
/// </summary>
/// <param name="Id">Stable identifier — survives renames and base-URL edits so chats
/// stay bound to the same logical endpoint across settings changes.</param>
/// <param name="Name">Optional user-supplied label. When blank, the UI derives one
/// from <see cref="BaseUrl"/> and <see cref="Model"/>.</param>
public sealed record EndpointSettings(
    Guid Id,
    string? Name,
    string BaseUrl,
    string ApiKey,
    string Model)
{
    public static EndpointSettings CreateDefault() => new(
        Id: Guid.NewGuid(),
        Name: null,
        BaseUrl: "http://localhost:8080/v1",
        ApiKey: "",
        Model: "");

    /// <summary>Best label for UI lists and tooltips. Falls back to "host · model"
    /// (or just the host) when <see cref="Name"/> is blank.</summary>
    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!.Trim();

            var host = TryGetHost(BaseUrl);
            var model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim();
            return (host, model) switch
            {
                (string h, string m) => $"{h} · {m}",
                (string h, null) => h,
                (null, string m) => m,
                _ => string.IsNullOrWhiteSpace(BaseUrl) ? "(unnamed endpoint)" : BaseUrl,
            };
        }
    }

    private static string? TryGetHost(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}
