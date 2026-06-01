using System.Text.Json;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

/// <summary>Dispatcher half of the deferred-tool mechanism. The model invokes a deferred
/// tool indirectly — <c>call_tool(name, arguments)</c> — so the tool's own schema never
/// enters the prefix tool array and the prompt-prefix cache survives.
///
/// This tool is intercepted by <see cref="Chat.AgentLoop"/>, which unwraps the inner tool
/// name + arguments and routes them through the normal permission / approval / execution
/// pipeline keyed on the real tool. <see cref="ExecuteAsync"/> is therefore never reached in
/// normal operation; it returns an error defensively in case the interception is bypassed.</summary>
public sealed class CallToolTool : ITool
{
    public const string ToolName = "call_tool";

    public string Name => ToolName;

    public string Description =>
        "Invoke a deferred tool by name. Use lookup_tool first to read the tool's argument " +
        "schema, then call call_tool with that tool's name and an 'arguments' object matching " +
        "its schema. Only deferred tools are invoked this way; regular tools are called directly.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "name": {
          "type": "string",
          "description": "Name of the deferred tool to invoke (as listed by lookup_tool)."
        },
        "arguments": {
          "type": "object",
          "description": "Arguments for the target tool, matching the schema returned by lookup_tool."
        }
      },
      "required": ["name"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedInSettings => false;

    public bool MandatoryEager => true;

    public bool CanBeHidden => false;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolResult.Error(
            "call_tool is dispatched by the agent loop and should not be executed directly."));
}
