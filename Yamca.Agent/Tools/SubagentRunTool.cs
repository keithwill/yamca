using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;

namespace Yamca.Agent.Tools;

/// <summary>Lets the parent LLM delegate a focused task to a configured subagent. The call
/// appears as one ordinary tool call; behind it, a headless subagent session runs with its own
/// instructions and curated, auto-allowed tools, and reports back through <c>subagent_result</c>.
/// The available agents are advertised inline in this tool's <see cref="Description"/> and the
/// <c>agent</c> parameter's enum/schema (rebuilt per round-trip from settings) so the model sees
/// the catalog exactly where it decides to call the tool. When no subagents are configured the
/// tool hides itself from the model entirely.</summary>
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

    // The effective, merged catalog the parent sees (project overrides user by name).
    private IReadOnlyList<SubagentDefinition> Agents =>
        SubagentRegistry.Merge(_settings.UserSubagents, _settings.ProjectSubagents);

    // Don't advertise an unusable tool: with no subagents configured there is nothing to
    // delegate to, so keep it out of the prompt entirely (and out of the prefix cache).
    public bool ExposedToLlm => Agents.Count > 0;

    public string Description
    {
        get
        {
            var sb = new StringBuilder(
                "Delegate a self-contained task to a configured subagent that runs in its own headless " +
                "session with a curated tool set, and return only its final answer. Prefer this for " +
                "well-scoped, context-heavy subtasks (codebase exploration, search, review, research): the " +
                "subagent's intermediate steps and tool output stay in its own session, so delegating keeps " +
                "this conversation's context small. Pass the subagent's 'agent' name and a complete, " +
                "self-contained 'prompt' — the subagent cannot see this conversation.");

            var agents = Agents;
            if (agents.Count > 0)
            {
                sb.Append(" Available subagents: ");
                sb.Append(string.Join(", ", agents.Select(a => a.Name)));
                sb.Append(" (see the 'agent' parameter for what each does).");
            }
            return sb.ToString();
        }
    }

    public string ParametersSchema
    {
        get
        {
            var agents = Agents;

            var agentProp = new JsonObject { ["type"] = "string" };
            if (agents.Count > 0)
            {
                var choices = new JsonArray();
                foreach (var a in agents) choices.Add(a.Name);
                agentProp["enum"] = choices;

                var desc = new StringBuilder("Which configured subagent to run. Options:");
                foreach (var a in agents)
                {
                    desc.Append("\n- ").Append(a.Name);
                    if (!string.IsNullOrWhiteSpace(a.Description))
                        desc.Append(": ").Append(a.Description.Trim());
                }
                agentProp["description"] = desc.ToString();
            }
            else
            {
                agentProp["description"] = "Name of the configured subagent to run.";
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["agent"] = agentProp,
                    ["prompt"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] =
                            "Complete, self-contained task or question for the subagent. Include every " +
                            "detail it needs — it cannot see this conversation, your files, or prior context.",
                    },
                },
                ["required"] = new JsonArray("agent", "prompt"),
                ["additionalProperties"] = false,
            };
            return schema.ToJsonString();
        }
    }

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
}
