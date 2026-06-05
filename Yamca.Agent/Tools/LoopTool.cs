using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;

namespace Yamca.Agent.Tools;

/// <summary>Fans one prompt out across many items, each handled by an isolated subagent session,
/// and returns a single roll-up of the outcomes. Behind the one tool call, a <see cref="BatchRunner"/>
/// runs N headless subagents and reduces their declared statuses mechanically — so the N transcripts
/// stay out of this conversation and only the aggregate (counts + failures) comes back. The available
/// agents are advertised inline (same as <c>subagent_run</c>); when none are configured the tool hides
/// itself. A loop is excluded from subagents' own tool sets, so loops cannot nest.</summary>
public sealed class LoopTool : ITool
{
    public const string ToolName = "loop";

    private readonly IBatchRunner _batch;
    private readonly ISessionSettings _settings;

    public LoopTool(IBatchRunner batch, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(settings);
        _batch = batch;
        _settings = settings;
    }

    public string Name => ToolName;

    // The effective, merged catalog the parent sees (project overrides user by name).
    private IReadOnlyList<SubagentDefinition> Agents =>
        SubagentRegistry.Merge(_settings.UserSubagents, _settings.ProjectSubagents);

    // Like subagent_run: with no subagents configured there is nothing to loop over, so keep the
    // tool out of the prompt entirely (and out of the prefix cache).
    public bool ExposedToLlm => Agents.Count > 0;

    public string Description
    {
        get
        {
            var sb = new StringBuilder(
@"Run one prompt over many items, each handled by its own isolated subagent session, and 
return a count of successes and failures. Use this only when EACH item is 
worth its own reasoning session. Pass the subagent's 'agent' name, a self-contained 'prompt', and the 'items'
array. Each item runs once and reports a status (success/failure/needs_followup) that the roll-up counts.  Your 'prompt' MUST
define success and failure conditions.");

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

                var desc = new StringBuilder("Which configured subagent to run for each item. Options:");
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
                agentProp["description"] = "Name of the configured subagent to run for each item.";
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
                            "The task applied to every item, self-contained — the subagent cannot see this " +
                            "conversation, your files, or prior context. Put the placeholder {{item}} where " +
                            "the item belongs and it is substituted in; if you omit it, the item is appended " +
                            "as \"Item: <item>\" instead. Define success vs failure explicitly so every item " +
                            "reports the status the same way, with the expected/nominal outcome as success.",
                    },
                    ["items"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] =
                            "The items to run the prompt over (e.g. filenames you enumerated first). One " +
                            $"isolated subagent session runs per item; at most {BatchRunner.MaxItems}.",
                    },
                },
                ["required"] = new JsonArray("agent", "prompt", "items"),
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
        if (!ToolArguments.TryGetStringArray(arguments, "items", out var items, out var itemsError))
            return ToolResult.Error(itemsError);

        return await _batch.RunAsync(agent, prompt, items, context, cancellationToken).ConfigureAwait(false);
    }
}
