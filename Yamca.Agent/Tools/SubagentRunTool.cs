using System.Text;
using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;

namespace Yamca.Agent.Tools;

/// <summary>Lets the parent LLM delegate a focused task to a configured subagent. The call
/// appears as one ordinary tool call; behind it, a headless subagent session runs with its own
/// instructions and curated, auto-allowed tools, and reports back through <c>subagent_result</c>.
/// The available agents are advertised to the parent via <see cref="SessionStartMessage"/>.</summary>
public sealed class SubagentRunTool : ITool
{
    public const string ToolName = "subagent_run";

    private readonly ISubagentRunner _runner;
    private readonly ISessionSettings _settings;

    public SubagentRunTool(ISubagentRunner runner, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(settings);
        _runner = runner;
        _settings = settings;
    }

    public string Name => ToolName;

    public string Description =>
        "Delegate a self-contained task to a configured subagent that runs in its own headless " +
        "session with a curated tool set, and return its answer. Pass the subagent's 'agent' name " +
        "and a complete 'prompt' describing the task. See the session-start note for the available " +
        "subagents and what each is for.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "agent":  { "type": "string", "description": "Name of the configured subagent to run." },
        "prompt": { "type": "string", "description": "Self-contained task/question for the subagent." }
      },
      "required": ["agent", "prompt"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "agent", out var agent, out var agentError))
            return ToolResult.Error(agentError);
        if (!ToolArguments.TryGetString(arguments, "prompt", out var prompt, out var promptError))
            return ToolResult.Error(promptError);

        return await _runner.RunAsync(agent, prompt, context, cancellationToken).ConfigureAwait(false);
    }

    public string? SessionStartMessage(ToolContext context)
    {
        var agents = SubagentRegistry.Merge(_settings.GlobalSubagents, _settings.ProjectSubagents);
        if (agents.Count == 0) return null;

        var sb = new StringBuilder("Subagents available via the subagent_run tool:");
        foreach (var a in agents)
        {
            sb.Append("\n- ").Append(a.Name);
            if (!string.IsNullOrWhiteSpace(a.Description))
                sb.Append(": ").Append(a.Description.Trim());
        }
        return sb.ToString();
    }
}
