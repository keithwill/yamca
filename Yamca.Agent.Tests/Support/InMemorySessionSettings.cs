using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Support;

internal sealed class InMemorySessionSettings : ISessionSettings
{
    public ToolSettingsMap Project { get; set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap Global { get; set; } = ToolSettingsMap.Empty;
    public EndpointSettings Endpoint { get; set; } = EndpointSettings.Default;
    public string SystemPrompt { get; set; } = "You are a test assistant.";
}
