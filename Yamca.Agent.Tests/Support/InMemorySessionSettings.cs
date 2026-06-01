using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Support;

internal sealed class InMemorySessionSettings : ISessionSettings
{
    public ToolSettingsMap Project { get; set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap Global { get; set; } = ToolSettingsMap.Empty;
    public EndpointsSettings Endpoints { get; set; } = EndpointsSettings.CreateDefault();
    public string SystemPrompt { get; set; } = "You are a test assistant.";
    public ScriptRegistry ProjectScripts { get; set; } = ScriptRegistry.Empty;
    public ScriptRegistry GlobalScripts { get; set; } = ScriptRegistry.Empty;
    public DeferredToolsHint DeferredToolsHint { get; set; } = DeferredToolsHint.Names;
}
