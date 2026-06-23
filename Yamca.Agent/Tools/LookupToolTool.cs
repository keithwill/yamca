using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tools;

/// <summary>Discovery half of the deferred-tool dispatcher. Deferred tools are never
/// placed in the prefix tool array (that would invalidate llama-server's prompt-prefix
/// cache the moment one is loaded). Instead the model:
/// <list type="number">
///   <item>sees the deferred catalog in the frozen session-start system message (see
///   <see cref="SessionStartMessage"/>) — names + summaries, the cue that a tool exists;</item>
///   <item>calls <c>lookup_tool</c> to read full argument schemas (returned as tool-result
///   content at the conversation tail — cache-safe);</item>
///   <item>invokes the tool via <c>call_tool</c> (see <see cref="CallToolTool"/>).</item>
/// </list>
/// Calling <c>lookup_tool</c> with no arguments lists the live catalog (including MCP tools
/// that connected after session start). With <c>tool_names</c> it returns those tools' schemas
/// and marks them loaded on the per-session <see cref="LoadedToolSet"/> so a subsequent
/// <c>call_tool</c> executes without a self-correction round-trip.</summary>
public sealed class LookupToolTool : ITool
{
    public const string ToolName = "lookup_tool";

    // Resolved lazily out of the service scope to break a DI cycle: ToolRegistry's factory
    // enumerates ITool services (including this one), so taking IToolRegistry directly would
    // close the loop and stall scope construction. Same pattern as ExecuteScriptTool.
    private readonly IServiceProvider _services;

    public LookupToolTool(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public string Name => ToolName;

    // Static description: the catalog of loadable names is NOT enumerated here. Doing so
    // would recompute per iteration (AgentLoop reads Description via ChatTool every round-trip)
    // and any change — e.g. an MCP server connecting mid-session — would mutate the prefix and
    // bust the cache. The live catalog is delivered as content instead (no-arg call below).
    public string Description =>
        "Discover deferred tools and read their argument schemas. Call with no arguments to " +
        "list the currently available deferred tools (name and summary). Pass 'tool_names' to " +
        "retrieve their full JSON argument schemas. After looking a tool up, invoke it with " +
        "call_tool.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "tool_names": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Optional. Names of deferred tools to fetch schemas for. Omit to list all available deferred tools."
        }
      },
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedInSettings => false;

    // Without lookup_tool in the initial schema the model cannot discover deferred tools at all.
    public bool MandatoryEager => true;

    public bool CanBeHidden => false;

    private IReadOnlyList<ITool> CurrentDeferred()
    {
        var registry = _services.GetRequiredService<IToolRegistry>();
        var availability = _services.GetRequiredService<IAvailabilityResolver>();
        return registry.GetDeferredTools(availability);
    }

    /// <summary>Frozen catalog snapshot folded into the single session-start system message.
    /// Computed once per fresh <see cref="ChatSession"/> (never per iteration), so it gives the
    /// model the "these tools exist" cue without ever mutating the prompt prefix mid-session. How
    /// much of the catalog is included is governed by the user-tier
    /// <see cref="ISessionSettings.DeferredToolsHint"/> so the user can trade discoverability
    /// against up-front context size.</summary>
    public string? SessionStartMessage(ToolContext context)
    {
        var hint = _services.GetService<ISessionSettings>()?.DeferredToolsHint ?? DeferredToolsHint.Names;

        var deferred = CurrentDeferred();
        var sb = new StringBuilder();
        // The mechanism hint is always present (even for None) so the model knows deferred tools
        // exist and how to reach them. Only the per-tool catalog below is gated by the setting.
        sb.Append("Some tools are deferred: their argument schemas are not loaded yet to keep the ");
        sb.Append("context small. To use one, call lookup_tool to read its schema, then call_tool ");
        sb.Append("to invoke it (do not call a deferred tool by name directly).");
        if (hint != DeferredToolsHint.None && deferred.Count > 0)
        {
            sb.Append("\n\nDeferred tools available at session start:\n");
            sb.Append(hint == DeferredToolsHint.NamesAndDescriptions
                ? DeferredToolCatalog.Summaries(deferred)
                : DeferredToolCatalog.Names(deferred));
        }
        sb.Append("\n\nAdditional deferred tools may become available during the session; ");
        sb.Append("call lookup_tool with no arguments to see the current list.");
        return sb.ToString();
    }

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var deferred = CurrentDeferred();

        // No names → list the live catalog (names + summaries).
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("tool_names", out var namesProp) ||
            namesProp.ValueKind != JsonValueKind.Array ||
            namesProp.GetArrayLength() == 0)
        {
            var listing = "Deferred tools (call call_tool to invoke one after reviewing its schema):\n"
                          + DeferredToolCatalog.Summaries(deferred);
            return Task.FromResult(ToolResult.Ok(listing));
        }

        var byName = deferred.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var found = new List<ITool>();
        var unknown = new List<string>();

        foreach (var item in namesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Task.FromResult(ToolResult.Error("Every entry in 'tool_names' must be a string."));

            var name = item.GetString() ?? string.Empty;
            if (byName.TryGetValue(name, out var tool))
            {
                if (!found.Contains(tool)) found.Add(tool);
                // Mark loaded on the session-owned set from the context (see ToolContext.LoadedTools)
                // so the agent loop's self-correction check sees the same instance.
                context.LoadedTools?.MarkLoaded(name);
            }
            else
            {
                unknown.Add(name);
            }
        }

        var sb = new StringBuilder();
        if (found.Count > 0)
        {
            sb.Append("Schemas (invoke with call_tool, e.g. call_tool(name=\"")
              .Append(found[0].Name)
              .Append("\", arguments={...})):\n");
            sb.Append(DeferredToolCatalog.Schemas(found));
        }
        if (unknown.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("Not a deferred tool: ").Append(string.Join(", ", unknown))
              .Append(".\nAvailable deferred tools:\n")
              .Append(DeferredToolCatalog.Summaries(deferred));
        }

        var result = found.Count == 0 ? ToolResult.Error(sb.ToString()) : ToolResult.Ok(sb.ToString());
        return Task.FromResult(result);
    }
}
