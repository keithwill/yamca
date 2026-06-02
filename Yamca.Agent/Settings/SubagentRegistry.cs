namespace Yamca.Agent.Settings;

/// <summary>One configured subagent. <see cref="Name"/> is the identifier the parent LLM
/// passes to the <c>subagent_run</c> tool; <see cref="Description"/> is advertised to the
/// parent so it knows when to delegate. <see cref="Instructions"/> become the subagent
/// session's system prompt, and <see cref="AllowedTools"/> is the set of tool names the
/// subagent may call (all auto-allowed, since it runs headless).</summary>
public sealed record SubagentDefinition(
    Guid Id,
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<string> AllowedTools,
    bool RestrictToWorkspace = true,
    bool RequireApproval = false,
    Guid? EndpointId = null,
    int? MaxIterations = null);

/// <summary>User-curated list of subagents the parent LLM may launch via the
/// <c>subagent_run</c> tool. Stored per tier (user + project) on disk and merged at the
/// use site (project overrides user by name).</summary>
public sealed class SubagentRegistry
{
    public static SubagentRegistry Empty { get; } = new(Array.Empty<SubagentDefinition>());

    public IReadOnlyList<SubagentDefinition> Agents { get; }

    public SubagentRegistry(IReadOnlyList<SubagentDefinition> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        Agents = agents;
    }

    public bool IsEmpty => Agents.Count == 0;

    /// <summary>Merge the two tiers into the effective list the parent sees: project entries
    /// replace user entries with the same <see cref="SubagentDefinition.Name"/> (ordinal,
    /// case-insensitive), then any project-only entries are appended. User ordering is
    /// otherwise preserved.</summary>
    public static IReadOnlyList<SubagentDefinition> Merge(SubagentRegistry user, SubagentRegistry project)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(project);

        var byName = new Dictionary<string, SubagentDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var a in user.Agents)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            if (!byName.ContainsKey(a.Name)) order.Add(a.Name);
            byName[a.Name] = a;
        }
        foreach (var a in project.Agents)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            if (!byName.ContainsKey(a.Name)) order.Add(a.Name);
            byName[a.Name] = a;
        }

        return order.Select(n => byName[n]).ToList();
    }

    /// <summary>Resolve a subagent by name from the merged list. Case-insensitive.</summary>
    public static SubagentDefinition? Resolve(SubagentRegistry user, SubagentRegistry project, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Merge(user, project)
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
