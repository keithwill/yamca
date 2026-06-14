using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Marks one of a card's tasks done (or, with done=false, un-ticks it).</summary>
public sealed class BoardCompleteTaskTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardCompleteTaskTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_complete_task";

    public string Description =>
        "Mark one of a card's tasks as done (tick it off). Identify the card by id and the task by its id " +
        "(see board_get_card for the ids). Pass done=false to un-tick a task instead.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string",  "description": "Card id (e.g. '7')." },
        "task": { "type": "integer", "description": "The task's card-local id (e.g. 2)." },
        "done": { "type": "boolean", "description": "Optional: false to un-tick the task. Defaults to true." }
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
        if (!ToolArguments.TryGetBool(arguments, "done", true, out var done, out var doneErr))
            return ToolResult.Error(doneErr);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        if (!await _boardStore.SetTaskDoneAsync(card.Id, taskId, done, cancellationToken))
            return ToolResult.Error($"Card #{card.Id} has no task #{taskId}.");

        return ToolResult.Ok($"Marked task #{taskId} on card #{card.Id} as {(done ? "done" : "not done")}.");
    }
}
