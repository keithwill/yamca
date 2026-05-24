using System.Text.Json;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Support;

/// <summary>A bare-bones <see cref="ITool"/> for agent-loop tests — captures every
/// invocation and returns whatever <see cref="Responder"/> chooses.</summary>
internal sealed class StubTool : ITool
{
    public StubTool(
        string name,
        PermissionLevel defaultPermission = PermissionLevel.Allow,
        Func<JsonElement, ToolContext, ToolResult>? responder = null)
    {
        Name = name;
        DefaultPermission = defaultPermission;
        Responder = responder ?? ((_, _) => ToolResult.Ok($"{name} ok"));
    }

    public string Name { get; }
    public string Description => $"stub tool {Name}";
    public string ParametersSchema => """{ "type":"object", "properties":{}, "additionalProperties": true }""";
    public bool SupportsWorkspaceRestriction => false;
    public PermissionLevel DefaultPermission { get; }
    public Func<JsonElement, ToolContext, ToolResult> Responder { get; set; }

    public List<(JsonElement Args, ToolContext Context)> Invocations { get; } = new();

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Invocations.Add((arguments.Clone(), context));
        return Task.FromResult(Responder(arguments, context));
    }
}
