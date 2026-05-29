using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Git;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Moves a card to another column by relocating its file (<c>git mv</c>). The move is
/// staged but NOT committed, so it can be bundled into the commit that completes the work.</summary>
public sealed class BoardMoveCardTool : ITool
{
    private readonly BoardService _board;
    private readonly GitService _git;

    public BoardMoveCardTool(BoardService board, GitService git)
    {
        _board = board;
        _git = git;
    }

    public string Name => "board_move_card";

    public string Description =>
        "Move a board card to another column by relocating its markdown file into that column's directory " +
        "(git mv). The move is staged but NOT committed — when finishing an implementation step, commit the " +
        "card move together with the code changes and any ticked subtasks in a single commit.";

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

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);
        if (!ToolArguments.TryGetString(arguments, "to_column", out var columnRef, out var colErr))
            return ToolResult.Error(colErr);

        var root = context.Workspace.RootPath;
        var snapshot = _board.Read(root);

        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var target = snapshot.FindColumn(columnRef);
        if (target is null)
            return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

        if (string.Equals(card.ColumnDirectory, target.DirectoryName, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok($"Card #{card.Id} is already in '{target.DisplayName}'.");

        var dest = Path.Combine(target.AbsolutePath, card.FileName);
        if (!ToolArguments.TryResolvePath(context, dest, out var resolvedDest, out var destErr))
            return ToolResult.Error(destErr);
        if (!ToolArguments.TryResolvePath(context, card.AbsolutePath, out var resolvedSrc, out var srcErr))
            return ToolResult.Error(srcErr);

        if (File.Exists(resolvedDest))
            return ToolResult.Error($"A card file named '{card.FileName}' already exists in '{target.DisplayName}'.");

        try { Directory.CreateDirectory(target.AbsolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Could not create column directory: {ex.Message}");
        }

        var mv = await _git.MoveAsync(root, resolvedSrc, resolvedDest, cancellationToken);
        if (mv.Ok)
            return ToolResult.Ok($"Moved card #{card.Id} from '{card.ColumnDirectory}' to '{target.DisplayName}'. The rename is staged (not committed) — commit it with the related code changes.");

        // git mv fails for a never-committed (untracked) card. Fall back to a filesystem move,
        // then best-effort stage the new location so it still rides along with the next commit.
        try
        {
            File.Move(resolvedSrc, resolvedDest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"git mv failed ({mv.Stderr.Trim()}) and the fallback move failed: {ex.Message}");
        }

        var add = await _git.AddAsync(root, resolvedDest, cancellationToken);
        var note = add.Ok ? "staged (not committed)" : "moved (not under git — history will not track the move)";
        return ToolResult.Ok($"Moved card #{card.Id} from '{card.ColumnDirectory}' to '{target.DisplayName}'; {note}.");
    }
}
