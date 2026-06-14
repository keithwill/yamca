using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns the full markdown of a board card (frontmatter + body, verbatim).</summary>
public sealed class BoardGetCardTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardGetCardTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_get_card";

    public string Description =>
        "Return the full, verbatim markdown of a board card (frontmatter and body, including any '- [ ]' subtask " +
        "checklist) so it can be read or edited. Identify the card by its id (e.g. '7').";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string", "description": "Card id (e.g. '7')." }
      },
      "required": ["card"],
      "additionalProperties": false
    }
    """;

    // The board lives at the repository root (which may sit above the session's sandbox root), so
    // board tools are never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public bool Deferred => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var argError))
            return ToolResult.Error(argError);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var column = snapshot.FindColumn(card.ColumnId);
        var columnName = column?.DisplayName ?? card.ColumnId;
        return ToolResult.Ok($"Card #{card.Id} in column '{columnName}':\n\n{CardMarkdown.Render(card)}");
    }
}
