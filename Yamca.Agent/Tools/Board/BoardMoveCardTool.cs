using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Moves a card to another column by relocating its file within the board directory.</summary>
public sealed class BoardMoveCardTool : ITool
{
    private readonly BoardService _board;
    private readonly BoardStore _boardStore;

    public BoardMoveCardTool(BoardService board, BoardStore boardStore)
    {
        _board = board;
        _boardStore = boardStore;
    }

    public string Name => "board_move_card";

    public string Description =>
        "Move a board card to another column by relocating its markdown file into that column's directory. " +
        "Commit your code changes on this branch first, then move the card.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":      { "type": "string", "description": "Card id (e.g. '0007') or file name." },
        "to_column": { "type": "string", "description": "Target column display name (e.g. 'verify') or directory name." }
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

        return await _boardStore.MutateAsync(async boardRoot =>
        {
            var snapshot = _board.Read(boardRoot);

            var card = snapshot.FindCard(cardRef);
            if (card is null)
                return ToolResult.Error($"No card matching '{cardRef}' on the board.");

            var target = snapshot.FindColumn(columnRef);
            if (target is null)
                return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

            if (string.Equals(card.ColumnDirectory, target.DirectoryName, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Ok($"Card #{card.Id} is already in '{target.DisplayName}'.");

            var src = Path.GetFullPath(card.AbsolutePath);
            var dest = Path.GetFullPath(Path.Combine(target.AbsolutePath, card.FileName));

            if (File.Exists(dest))
                return ToolResult.Error($"A card file named '{card.FileName}' already exists in '{target.DisplayName}'.");

            try
            {
                Directory.CreateDirectory(target.AbsolutePath);
                File.Move(src, dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error($"Could not move card #{card.Id}: {ex.Message}");
            }

            return ToolResult.Ok($"Moved card #{card.Id} from '{card.ColumnDirectory}' to '{target.DisplayName}'.");
        }, cancellationToken);
    }
}
