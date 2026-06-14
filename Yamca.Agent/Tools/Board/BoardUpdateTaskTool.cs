using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Rewrites the text of one of a card's tasks (its done flag is unchanged).</summary>
public sealed class BoardUpdateTaskTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardUpdateTaskTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_update_task";

    public string Description =>
        "Change the wording of one of a card's tasks. Identify the card by id and the task by its id (see " +
        "board_get_card for the ids). The task's done state is unchanged. To tick/un-tick use board_complete_task.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string",  "description": "Card id (e.g. '7')." },
        "task": { "type": "integer", "description": "The task's card-local id (e.g. 2)." },
        "text": { "type": "string",  "description": "The new task text." }
      },
      "required": ["card", "task", "text"],
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
        if (!ToolArguments.TryGetString(arguments, "text", out var text, out var textErr))
            return ToolResult.Error(textErr);
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Error("Task text cannot be empty.");

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        if (!await _boardStore.UpdateTaskTextAsync(card.Id, taskId, text, cancellationToken))
            return ToolResult.Error($"Card #{card.Id} has no task #{taskId}.");

        return ToolResult.Ok($"Updated task #{taskId} on card #{card.Id}.");
    }
}
