using Yamca.Agent.Settings;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Support;

internal sealed class InMemorySessionSettings : ISessionSettings
{
    public ToolSettingsMap Project { get; set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap User { get; set; } = ToolSettingsMap.Empty;
    public EndpointsSettings Endpoints { get; set; } = EndpointsSettings.CreateDefault();
    public string SystemPrompt { get; set; } = "You are a test assistant.";
    public ScriptRegistry ProjectScripts { get; set; } = ScriptRegistry.Empty;
    public ScriptRegistry UserScripts { get; set; } = ScriptRegistry.Empty;
    public SubagentRegistry ProjectSubagents { get; set; } = SubagentRegistry.Empty;
    public SubagentRegistry UserSubagents { get; set; } = SubagentRegistry.Empty;
    public DeferredToolsHint DeferredToolsHint { get; set; } = DeferredToolsHint.Names;
    public int MaxToolIterations { get; set; } = 10;
    public ShellPreference ShellPreference { get; set; } = ShellPreference.Auto;
    public OrchestratorSettings Orchestrator { get; set; } = OrchestratorSettings.Default;
    public bool MetricsEnabled { get; set; } = true;
}
