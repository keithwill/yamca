using System.Text;
using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Appends one or more tasks (a card's child checklist items) to a board card, each assigned
/// a stable card-local id.</summary>
public sealed class BoardAddTasksTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardAddTasksTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_add_tasks";

    public string Description =>
        "Add one or more tasks to a board card's checklist. Each task gets a stable card-local id used to " +
        "complete, edit, or remove it later. Pass several at once when planning out the work. Tasks are kept " +
        "off the card body — the body stays the feature/issue description.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":  { "type": "string", "description": "Card id (e.g. '7')." },
        "tasks": {
          "type": "array",
          "items": { "type": "string" },
          "description": "The task texts to add, in order (e.g. ['Write tests', 'Update docs'])."
        }
      },
      "required": ["card", "tasks"],
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
        if (!ToolArguments.TryGetStringArray(arguments, "tasks", out var texts, out var tasksErr))
            return ToolResult.Error(tasksErr);
        if (texts.All(t => string.IsNullOrWhiteSpace(t)))
            return ToolResult.Error("Provide at least one non-empty task.");

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var tasks = await _boardStore.AddTasksAsync(card.Id, texts, cancellationToken);
        if (tasks is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var sb = new StringBuilder();
        sb.Append("Card #").Append(card.Id).Append(" now has ").Append(tasks.Count).Append(" task(s):\n");
        foreach (var task in tasks)
            sb.Append("- #").Append(task.Id).Append(" [").Append(task.Done ? 'x' : ' ').Append("] ").Append(task.Text).Append('\n');
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}
