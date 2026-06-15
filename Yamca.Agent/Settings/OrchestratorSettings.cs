namespace Yamca.Agent.Settings;

/// <summary>
/// Configuration for the board orchestrator — the background service that autonomously
/// picks up cards in enabled work columns and runs them through a headless agent loop.
/// Stored in the project tier (the board is repo-anchored, so which columns are automatable
/// is a property of this repository's workflow); the endpoint is referenced by id into the
/// user-tier endpoint list, the same way <see cref="SubagentDefinition.EndpointId"/> is.
/// The orchestrator re-reads these settings every poll tick, so changes apply to future
/// dispatch without a restart.
/// </summary>
public sealed record OrchestratorSettings(
    /// <summary>Board column <em>directory</em> names (e.g. <c>20-analyze</c>) whose cards are
    /// dispatched autonomously. Directory names are the stable column identity; the UI shows
    /// display names but stores these.</summary>
    IReadOnlyList<string> EnabledColumns,

    /// <summary>Endpoint used for orchestrated runs, or null for the configured default endpoint.</summary>
    Guid? EndpointId,

    /// <summary>Global cap on concurrently running cards.</summary>
    int MaxConcurrentRuns,

    /// <summary>Optional per-column cap on concurrently running cards; null means only the
    /// global cap applies.</summary>
    int? MaxConcurrentRunsPerColumn,

    /// <summary>Maximum turns per run: the seed turn plus continuation turns issued when the
    /// agent stops without moving the card.</summary>
    int MaxTurnsPerRun,

    /// <summary>Tool-iteration cap per turn, or null to use the session-wide MaxToolIterations.</summary>
    int? MaxToolIterationsPerTurn,

    /// <summary>A turn that produces no stream events for this long is cancelled and retried.</summary>
    int StallTimeoutSeconds,

    /// <summary>Absolute wall-clock limit for a single turn.</summary>
    int TurnTimeoutSeconds,

    /// <summary>Failed runs are retried up to this many attempts, then the card is parked
    /// (skipped until the user moves/edits it or toggles the orchestrator).</summary>
    int RetryMaxAttempts,

    /// <summary>First retry delay; subsequent retries double it.</summary>
    int RetryBaseDelaySeconds,

    /// <summary>Ceiling for the exponential retry backoff.</summary>
    int RetryMaxDelaySeconds,

    /// <summary>How often the orchestrator polls the board for work and reconciles running cards.</summary>
    int PollIntervalSeconds,

    /// <summary>Tools available to orchestrated runs — every tool in this list is auto-allowed
    /// (no approval prompts). <c>subagent_run</c> and <c>loop</c> are always excluded at runner
    /// build time regardless of this list.</summary>
    IReadOnlyList<string> AllowedTools,

    /// <summary>Restrict tools that support it to the run's worktree workspace.</summary>
    bool RestrictToWorkspace)
{
    /// <summary>The curated tool set orchestrated runs start with: file and search tools, the
    /// board tools (board_move_card is the completion signal), allowed commands/scripts, and git
    /// (column instructions tell the agent to commit on the card's branch).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedTools = new[]
    {
        "read_file", "write_file", "edit_file", "delete_file",
        "list_directory", "find_files", "grep",
        "execute_allowed", "git",
        "board_list", "board_get_card", "board_get_step_instructions",
        "board_move_card", "board_update_card",
    };

    public static OrchestratorSettings Default { get; } = new(
        EnabledColumns: Array.Empty<string>(),
        EndpointId: null,
        MaxConcurrentRuns: 2,
        MaxConcurrentRunsPerColumn: null,
        MaxTurnsPerRun: 4,
        MaxToolIterationsPerTurn: null,
        StallTimeoutSeconds: 300,
        TurnTimeoutSeconds: 1800,
        RetryMaxAttempts: 3,
        RetryBaseDelaySeconds: 30,
        RetryMaxDelaySeconds: 600,
        PollIntervalSeconds: 10,
        AllowedTools: DefaultAllowedTools,
        RestrictToWorkspace: true);
}
