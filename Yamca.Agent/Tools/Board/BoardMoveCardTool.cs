using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Git;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Moves a card to another column by relocating its file within the board worktree, then
/// commits the move to the board branch and stamps it with the code commit it corresponds to.</summary>
public sealed class BoardMoveCardTool : ITool
{
    private readonly BoardService _board;
    private readonly BoardWorktree _boardWorktree;
    private readonly GitService _git;

    public BoardMoveCardTool(BoardService board, BoardWorktree boardWorktree, GitService git)
    {
        _board = board;
        _boardWorktree = boardWorktree;
        _git = git;
    }

    public string Name => "board_move_card";

    public string Description =>
        "Move a board card to another column by relocating its markdown file into that column's directory. " +
        "The move is committed to the board branch automatically (the board is tracked separately from your code) " +
        "and records the latest commit on your current code branch, so the status change stays linked to the code " +
        "it corresponds to. Commit your code changes first, then move the card.";

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

    // The board is a worktree of the yamca-board orphan branch, resolved from the repository root
    // (which may sit above the session's sandbox root). Board tools are never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);
        if (!ToolArguments.TryGetString(arguments, "to_column", out var columnRef, out var colErr))
            return ToolResult.Error(colErr);

        // Read the code worktree's HEAD before taking the board lock: this is the association stamp
        // the move records. Best-effort — a plain chat session or detached/empty HEAD simply skips it.
        var codeHead = await _git.RevParseHeadAsync(context.Workspace.RootPath, cancellationToken);

        return await _boardWorktree.MutateAsync(async boardRoot =>
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
                // The board worktree holds only board files and everything is committed, so a plain
                // filesystem move is enough — git detects the rename when CommitAllAsync stages it.
                File.Move(src, dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error($"Could not move card #{card.Id}: {ex.Message}");
            }

            // Association stamp: record the code commit in the card's frontmatter and the board
            // commit message, recovering the code↔status link without co-committing the two trees.
            var message = $"board: move #{card.Id} to {target.DisplayName}";
            if (codeHead is { } head)
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(dest, cancellationToken);
                    await File.WriteAllTextAsync(dest, BoardService.WithCommit(raw, head.Sha), cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The move still stands; just skip the frontmatter stamp on a write failure.
                }
                var codeRef = head.Branch is null ? head.Sha : $"{head.Branch}@{head.Sha}";
                message += $"\n\nCode: {codeRef}";
            }

            var commit = await _git.CommitAllAsync(boardRoot, message, cancellationToken);
            if (!commit.Ok)
                return ToolResult.Error($"Card #{card.Id} moved but the board commit failed: {commit.Stderr.Trim()}");

            var stamp = codeHead is { } h ? $"; recorded code commit {h.Sha[..Math.Min(7, h.Sha.Length)]}" : "";
            return ToolResult.Ok($"Moved card #{card.Id} from '{card.ColumnDirectory}' to '{target.DisplayName}' and committed to the board branch{stamp}.");
        }, cancellationToken);
    }
}
