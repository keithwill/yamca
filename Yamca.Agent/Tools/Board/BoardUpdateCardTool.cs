using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Replaces a card's full markdown content — used to refine the plan or tick subtasks.</summary>
public sealed class BoardUpdateCardTool : ITool
{
    private readonly BoardService _board;

    public BoardUpdateCardTool(BoardService board) => _board = board;

    public string Name => "board_update_card";

    public string Description =>
        "Replace a board card's full markdown content (frontmatter + body). Use this to refine the plan, add or " +
        "tick subtasks ('- [ ]' → '- [x]'), etc. Fetch the current content with board_get_card first, edit it, and " +
        "pass the complete new content. This writes the working tree only; commit it with your related changes.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":    { "type": "string", "description": "Card id (e.g. '0007') or file name." },
        "content": { "type": "string", "description": "The complete new markdown content for the card file." }
      },
      "required": ["card", "content"],
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);
        if (!ToolArguments.TryGetString(arguments, "content", out var content, out var contentErr))
            return ToolResult.Error(contentErr);

        var snapshot = _board.Read(context.Workspace.RootPath);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        if (!ToolArguments.TryResolvePath(context, card.AbsolutePath, out var resolved, out var pathError))
            return ToolResult.Error(pathError);

        try
        {
            await File.WriteAllTextAsync(resolved, content, cancellationToken);
            return ToolResult.Ok($"Updated card #{card.Id} ({card.FileName}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to update card '{card.FileName}': {ex.Message}");
        }
    }
}
