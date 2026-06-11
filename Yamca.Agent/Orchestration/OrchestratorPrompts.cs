namespace Yamca.Agent.Orchestration;

/// <summary>Static text fragments for orchestrated runs, mirroring
/// <see cref="Chat.Prompts.SubagentPrompts"/>: the preamble is a byte-stable <c>const</c> so it
/// forms a shared prompt prefix across every orchestrated run. The seed prompt itself reuses
/// <see cref="Board.BoardPrompts.BuildSeedPrompt"/> — column instructions remain the single
/// source of the step's completion protocol.</summary>
public static class OrchestratorPrompts
{
    /// <summary>Fixed system prompt for orchestrated runs. Byte-stable by contract.</summary>
    public const string HeadlessPreamble =
        "You are running headless against a Kanban dev-board card — there is no user to talk " +
        "to. Work the card according to the step instructions you are given. Do not ask " +
        "clarifying questions: make reasonable assumptions and proceed. The run is complete " +
        "only when you move the card to the next column with the board_move_card tool; commit " +
        "your code changes on this branch first when the instructions call for it. If you are " +
        "blocked and cannot complete the step, state exactly why in your final message instead " +
        "of moving the card. Your responses are not rendered as Markdown.";

    /// <summary>Continuation message issued when the agent ends a turn without moving the card
    /// (the orchestrator analog of the subagent nudge).</summary>
    public const string ContinuationPrompt =
        "You stopped without moving the card to the next column. If the work is finished, " +
        "commit your changes and call board_move_card now. If it is not finished, continue " +
        "working. If you are blocked and cannot complete this step, say exactly why in your " +
        "final message.";
}
