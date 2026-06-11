using Yamca.Agent.Chat;

namespace Yamca.Agent.Orchestration;

/// <summary>Identity and context of one orchestrated run attempt, published when it starts.</summary>
public sealed record OrchestratorRunInfo(
    string RunId,
    string CardId,
    string CardTitle,
    string ColumnDirectory,
    string ColumnDisplayName,
    string Branch,
    string WorktreePath,
    int Attempt,
    string SeedPrompt,
    DateTimeOffset StartedAt);

/// <summary>Mirror of an orchestrated run's lifecycle, deliberately parallel to
/// <see cref="Subagents.ISubagentObserver"/> but multi-turn aware: a run spans the seed turn
/// plus any continuation turns, and <see cref="OnTurnStarted"/> marks each one. Callbacks
/// arrive on background continuations; implementations must be thread-safe.</summary>
public interface IOrchestratorObserver
{
    void OnRunStarted(OrchestratorRunInfo info);

    /// <summary>A turn began: the seed prompt for the first turn, the continuation message for
    /// later ones. Not raised for iteration-cap resumes, which continue the current turn.</summary>
    void OnTurnStarted(string runId, string userMessage);

    void OnRunEvent(string runId, ChatStreamEvent ev);

    void OnRunCompleted(string runId, OrchestratorRunOutcome outcome);
}

/// <summary>Observer that ignores everything (headless runs without a UI).</summary>
public sealed class NoopOrchestratorObserver : IOrchestratorObserver
{
    public static NoopOrchestratorObserver Instance { get; } = new();
    public void OnRunStarted(OrchestratorRunInfo info) { }
    public void OnTurnStarted(string runId, string userMessage) { }
    public void OnRunEvent(string runId, ChatStreamEvent ev) { }
    public void OnRunCompleted(string runId, OrchestratorRunOutcome outcome) { }
}
