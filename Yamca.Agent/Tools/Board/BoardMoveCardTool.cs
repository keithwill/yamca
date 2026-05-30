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

    // The dev board lives at .yamca/board under the git repository root, which may sit above the
    // session's sandbox root. Board tools are therefore never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);
        if (!ToolArguments.TryGetString(arguments, "to_column", out var columnRef, out var colErr))
            return ToolResult.Error(colErr);

        var root = context.Workspace.RepositoryRoot;
        var snapshot = _board.Read(root);

        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var target = snapshot.FindColumn(columnRef);
        if (target is null)
            return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

        if (string.Equals(card.ColumnDirectory, target.DirectoryName, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok($"Card #{card.Id} is already in '{target.DisplayName}'.");

        // Card/column paths come from BoardService's enumeration of the repository board directory,
        // so they are already absolute and trusted. They are NOT clamped to the sandbox: the board
        // lives at the repository root, which may sit above the session's workspace root.
        var resolvedSrc = Path.GetFullPath(card.AbsolutePath);
        var resolvedDest = Path.GetFullPath(Path.Combine(target.AbsolutePath, card.FileName));

        if (File.Exists(resolvedDest))
            return ToolResult.Error($"A card file named '{card.FileName}' already exists in '{target.DisplayName}'.");

        try { Directory.CreateDirectory(target.AbsolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Could not create column directory: {ex.Message}");
        }

        // Move the card file, staging the relocation but NOT committing it — the agent bundles the
        // move into the commit that completes the step, alongside the code changes and ticked
        // subtasks. (The board UI shares this primitive but commits the move in isolation instead.)
        var moved = await _git.MoveWithUntrackedFallbackAsync(root, resolvedSrc, resolvedDest, cancellationToken);
        if (!moved.Ok)
            return ToolResult.Error(moved.Error);

        var note = moved.Staged ? "staged (not committed)" : "moved (not under git — history will not track the move)";
        return ToolResult.Ok($"Moved card #{card.Id} from '{card.ColumnDirectory}' to '{target.DisplayName}'; {note}.");
    }
}
