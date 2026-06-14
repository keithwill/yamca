using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Replaces a card's full markdown content — used to refine the plan or tick subtasks.</summary>
public sealed class BoardUpdateCardTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardUpdateCardTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_update_card";

    public string Description =>
        "Replace a board card's full markdown content (frontmatter + body). Use this to refine the plan, add or " +
        "tick subtasks ('- [ ]' → '- [x]'), etc. Fetch the current content with board_get_card first, edit it, and " +
        "pass the complete new content.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":    { "type": "string", "description": "Card id (e.g. '7')." },
        "content": { "type": "string", "description": "The complete new markdown content for the card file." }
      },
      "required": ["card", "content"],
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
        if (!ToolArguments.TryGetString(arguments, "content", out var content, out var contentErr))
            return ToolResult.Error(contentErr);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var parsed = CardMarkdown.Parse(content);
        if (!await _boardStore.UpdateCardContentAsync(card.Id, parsed, cancellationToken))
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        return ToolResult.Ok($"Updated card #{card.Id}.");
    }
}
