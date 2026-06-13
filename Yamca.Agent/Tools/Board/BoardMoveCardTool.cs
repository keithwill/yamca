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
        "Move a board card to another column. " +
        "Commit your code changes on this branch first, then move the card.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":      { "type": "string", "description": "Card id (e.g. '0007')." },
        "to_column": { "type": "string", "description": "Target column display name (e.g. 'verify') or id." }
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

        var target = snapshot.FindColumn(columnRef);
        if (target is null)
            return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

        var from = snapshot.FindColumn(card.ColumnId);
        if (string.Equals(card.ColumnId, target.Id, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok($"Card #{card.Id} is already in '{target.DisplayName}'.");

        if (!await _boardStore.MoveCardAsync(card.Id, target.Id, cancellationToken))
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        return ToolResult.Ok($"Moved card #{card.Id} from '{from?.DisplayName ?? card.ColumnId}' to '{target.DisplayName}'.");
    }
}
