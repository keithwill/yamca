using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns the full markdown of a board card (frontmatter + body, verbatim).</summary>
public sealed class BoardGetCardTool : ITool
{
    private readonly BoardService _board;
    private readonly BoardWorktree _boardWorktree;

    public BoardGetCardTool(BoardService board, BoardWorktree boardWorktree)
    {
        _board = board;
        _boardWorktree = boardWorktree;
    }

    public string Name => "board_get_card";

    public string Description =>
        "Return the full, verbatim markdown of a board card (frontmatter and body, including any '- [ ]' subtask " +
        "checklist) so it can be read or edited. Identify the card by its id (e.g. '7' or '0007') or file name.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string", "description": "Card id (e.g. '0007') or file name." }
      },
      "required": ["card"],
      "additionalProperties": false
    }
    """;

    // The board is a worktree of the yamca-board orphan branch, resolved from the repository root
    // (which may sit above the session's sandbox root). Board tools are never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var argError))
            return ToolResult.Error(argError);

        var boardRoot = await _boardWorktree.EnsureAsync(cancellationToken);
        var snapshot = _board.Read(boardRoot);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        // The card path comes from BoardService's enumeration of the board worktree, so it is
        // already absolute and trusted. It is NOT clamped to the sandbox: the board worktree lives
        // under the repository root, which may sit above the session's workspace root.
        var resolved = Path.GetFullPath(card.AbsolutePath);

        try
        {
            var text = await File.ReadAllTextAsync(resolved, cancellationToken);
            return ToolResult.Ok($"Card #{card.Id} in column '{card.ColumnDirectory}' ({card.FileName}):\n\n{text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read card '{card.FileName}': {ex.Message}");
        }
    }
}
