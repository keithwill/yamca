namespace Yamca.Agent.Board;

/// <summary>Pure builders for the prompts/instructions used when launching a chat session to
/// work a board step. Kept in the agent layer (no UI/session dependencies) so they are unit-testable.</summary>
public static class BoardPrompts
{
    /// <summary>The draft prompt that kicks off a step run, pre-filled into the composer. Passes the
    /// step's own instructions plus the bare card ID — deliberately *not* the card title or body.
    /// Inlining the card details tended to confuse the LLM (it would re-fetch the card via tool calls
    /// anyway); the column instructions are the single source of truth for what to do with the card ID.
    /// Completion guidance (commit / board_move_card) lives in those instructions, so it is not
    /// duplicated here.</summary>
    public static string BuildSeedPrompt(BoardCard card, BoardColumn current, string? instructions)
    {
        return
$@"{instructions}

Card ID: {card.Id}";
    }
}
