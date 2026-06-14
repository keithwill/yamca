using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Deletes one of a card's tasks. The id is retired, never reused.</summary>
public sealed class BoardRemoveTaskTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardRemoveTaskTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_remove_task";

    public string Description =>
        "Delete one of a card's tasks. Identify the card by id and the task by its id (see board_get_card for " +
        "the ids). The id is retired and never reused.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string",  "description": "Card id (e.g. '7')." },
        "task": { "type": "integer", "description": "The task's card-local id (e.g. 2)." }
      },
      "required": ["card", "task"],
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
        if (!ToolArguments.TryGetInt(arguments, "task", out var taskId, out var taskErr))
            return ToolResult.Error(taskErr);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        if (!await _boardStore.RemoveTaskAsync(card.Id, taskId, cancellationToken))
            return ToolResult.Error($"Card #{card.Id} has no task #{taskId}.");

        return ToolResult.Ok($"Removed task #{taskId} from card #{card.Id}.");
    }
}
