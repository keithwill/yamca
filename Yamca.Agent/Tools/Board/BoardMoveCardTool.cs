using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Moves a card to another column by relocating its file within the board directory.</summary>
public sealed class BoardMoveCardTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardMoveCardTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_move_card";

    public string Description =>
        "Move a board card to another column. Pass the special values 'next' or 'previous' to move the " +
        "card one step forward or backward relative to its current column — preferred for step workflows, " +
        "so you needn't look up column names. Commit your code changes on this branch first, then move the card.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":      { "type": "string", "description": "Card id (e.g. '7')." },
        "to_column": { "type": "string", "description": "Target column: 'next' or 'previous' to move one step relative to the card's current column, or a column display name (e.g. 'verify') or id." }
      },
      "required": ["card", "to_column"],
      "additionalProperties": false
    }
    """;

    // The board lives at the repository root (which may sit above the session's sandbox root), so
    // board tools are never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);
        if (!ToolArguments.TryGetString(arguments, "to_column", out var columnRef, out var colErr))
            return ToolResult.Error(colErr);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);

        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var from = snapshot.FindColumn(card.ColumnId);

        // Resolve the target column. The special tokens 'next'/'previous' move the card one step
        // relative to its current column, so column instructions don't have to hard-code (or look up)
        // the destination column's name — keeping them robust to column reconfiguration.
        var token = columnRef.Trim();
        BoardColumn? target;
        if (string.Equals(token, "next", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "previous", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "prev", StringComparison.OrdinalIgnoreCase))
        {
            if (from is null)
                return ToolResult.Error($"Card #{card.Id} is in an unknown column '{card.ColumnId}', so '{token}' can't be resolved. Specify a target column by name.");

            var forward = token.StartsWith("next", StringComparison.OrdinalIgnoreCase);
            target = forward ? snapshot.NextColumn(from) : snapshot.PreviousColumn(from);
            if (target is null)
                return ToolResult.Ok($"Card #{card.Id} is already in the {(forward ? "last" : "first")} column ('{from.DisplayName}'); no {(forward ? "next" : "previous")} column to move to.");
        }
        else
        {
            target = snapshot.FindColumn(columnRef);
            if (target is null)
                return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");
        }

        if (string.Equals(card.ColumnId, target.Id, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok($"Card #{card.Id} is already in '{target.DisplayName}'.");

        if (!await _boardStore.MoveCardAsync(card.Id, target.Id, cancellationToken))
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        return ToolResult.Ok($"Moved card #{card.Id} from '{from?.DisplayName ?? card.ColumnId}' to '{target.DisplayName}'.");
    }
}
