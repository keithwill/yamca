namespace Yamca.Agent.Settings;

/// <summary>
/// OpenAI-compatible endpoint configuration. The agent is local-LLM first, so
/// <see cref="BaseUrl"/> typically points at a llama.cpp or vllm server.
/// </summary>
public sealed record EndpointSettings(string BaseUrl, string ApiKey, string Model)
{
    public static EndpointSettings Default { get; } = new(
        BaseUrl: "http://localhost:8080/v1",
        ApiKey: "",
        Model: "local-model");
}
