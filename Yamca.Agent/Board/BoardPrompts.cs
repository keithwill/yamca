namespace Yamca.Agent.Board;

/// <summary>Pure builders for the prompts/instructions used when launching a chat session to
/// work a board step. Kept in the agent layer (no UI/session dependencies) so they are unit-testable.</summary>
public static class BoardPrompts
{
    /// <summary>The draft prompt that kicks off a step run, pre-filled into the composer. Inlines the
    /// card (title + body) and the step's own instructions so the session is self-contained — no system
    /// seed message and no extra board_get_card round-trip. Completion guidance (commit / board_move_card)
    /// lives in each column's instructions.md, so it is not duplicated here.</summary>
    public static string BuildSeedPrompt(BoardCard card, BoardColumn current, string? instructions)
    {
        var prompt =
            $"Work the \"{current.DisplayName}\" step for board card #{card.Id} \"{card.Title}\".\n\n" +
            $"## Card\n\n{card.Body}";

        if (!string.IsNullOrWhiteSpace(instructions))
            prompt += $"\n\n## \"{current.DisplayName}\" step instructions\n\n{instructions}";

        return prompt;
    }
}
