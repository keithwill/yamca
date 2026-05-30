namespace Yamca.Agent.Board;

/// <summary>Pure builders for the prompts/instructions used when launching a chat session to
/// work a board step. Kept in the agent layer (no UI/session dependencies) so they are unit-testable.</summary>
public static class BoardPrompts
{
    /// <summary>The draft prompt that kicks off a step run, pre-filled into the composer for review.</summary>
    public static string BuildSeedPrompt(BoardCard card, BoardColumn current, BoardColumn? next)
    {
        var moveLine = next is null
            ? "This is the final column, so there is no further column to move the card to."
            : $"When this step is complete: tick any finished subtasks with board_update_card, commit your code " +
              $"changes on this branch, then move the card to \"{next.DisplayName}\" with board_move_card (the board " +
              $"is tracked separately; the move records your latest commit and is committed for you).";

        return
            $"Work the \"{current.DisplayName}\" step for board card #{card.Id} \"{card.Title}\".\n\n" +
            $"The instructions for the \"{current.DisplayName}\" step are in your system context. " +
            "Use board_get_card to read the card's current plan and subtasks, and board_list to see the board.\n\n" +
            moveLine;
    }

    /// <summary>Wraps a column's instructions.md as a system seed instruction for the session.</summary>
    public static string BuildStepInstruction(BoardColumn current, string instructions)
        => $"# Board step: {current.DisplayName}\n\n{instructions}";
}
