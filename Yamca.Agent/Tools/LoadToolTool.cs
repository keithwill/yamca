using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools;

/// <summary>Meta-tool that lazy-loads schemas for tools marked <see cref="ITool.Deferred"/>.
/// The initial tool list sent to the LLM omits deferred tools — only their names appear
/// in this tool's <see cref="Description"/>. Calling <c>load_tool</c> with one or more
/// names marks them as loaded on the per-session <see cref="LoadedToolSet"/>, and the
/// next iteration of <see cref="AgentLoop"/> includes their full schemas.</summary>
public sealed class LoadToolTool : ITool
{
    // Resolved lazily out of the service scope to break a DI cycle: ToolRegistry's
    // factory enumerates ITool services (including this one), so taking IToolRegistry
    // directly would close the loop and stall scope construction. See ExecuteScriptTool
    // for the same pattern.
    private readonly IServiceProvider _services;
    private readonly LoadedToolSet _loaded;
    private HashSet<string>? _deferredNamesCache;
    private string? _descriptionCache;

    public LoadToolTool(IServiceProvider services, LoadedToolSet loaded)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loaded);
        _services = services;
        _loaded = loaded;
    }

    private HashSet<string> DeferredNames
    {
        get
        {
            EnsureCached();
            return _deferredNamesCache!;
        }
    }

    private void EnsureCached()
    {
        if (_deferredNamesCache is not null) return;

        var registry = _services.GetRequiredService<IToolRegistry>();
        var deferred = registry.GetDeferredTools();
        _deferredNamesCache = new HashSet<string>(deferred.Select(t => t.Name), StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append("Load schemas for one or more deferred tools so they become callable for the rest of this session. ");
        if (_deferredNamesCache.Count == 0)
        {
            sb.Append("No deferred tools are currently registered.");
        }
        else
        {
            sb.Append("Deferred tools: ");
            sb.Append(string.Join(", ", deferred.Select(t => t.Name)));
            sb.Append('.');
        }
        _descriptionCache = sb.ToString();
    }

    public string Name => "load_tool";

    public string Description
    {
        get
        {
            EnsureCached();
            return _descriptionCache!;
        }
    }

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "tool_names": {
          "type": "array",
          "items": { "type": "string" },
          "minItems": 1,
          "description": "Names of deferred tools to load. After loading, the tools become available on the next iteration."
        }
      },
      "required": ["tool_names"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool ExposedInSettings => false;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("tool_names", out var namesProp) ||
            namesProp.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(ToolResult.Error("Argument 'tool_names' must be an array of strings."));
        }

        var loadedNow = new List<string>();
        var alreadyLoaded = new List<string>();
        var unknown = new List<string>();

        foreach (var item in namesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Task.FromResult(ToolResult.Error("Every entry in 'tool_names' must be a string."));

            var name = item.GetString() ?? string.Empty;
            if (!DeferredNames.Contains(name))
            {
                unknown.Add(name);
                continue;
            }

            if (_loaded.MarkLoaded(name))
                loadedNow.Add(name);
            else
                alreadyLoaded.Add(name);
        }

        var sb = new StringBuilder();
        if (loadedNow.Count > 0)
            sb.Append("Loaded: ").Append(string.Join(", ", loadedNow)).Append('.');
        if (alreadyLoaded.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("Already loaded: ").Append(string.Join(", ", alreadyLoaded)).Append('.');
        }
        if (unknown.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("Not a deferred tool: ").Append(string.Join(", ", unknown))
              .Append(". Deferred tools are: ")
              .Append(DeferredNames.Count == 0 ? "(none)" : string.Join(", ", DeferredNames))
              .Append('.');
        }

        if (sb.Length == 0)
            sb.Append("No tool names provided.");

        sb.Append(" Newly loaded tool schemas will appear in the tool list on the next iteration.");

        return Task.FromResult(unknown.Count > 0 ? ToolResult.Error(sb.ToString()) : ToolResult.Ok(sb.ToString()));
    }
}
