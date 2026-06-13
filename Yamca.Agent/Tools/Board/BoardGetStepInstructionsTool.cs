using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns a column's step instructions — the work normally done in that step.</summary>
public sealed class BoardGetStepInstructionsTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardGetStepInstructionsTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_get_step_instructions";

    public string Description =>
        "Return the step instructions for a board column — guidance on the work normally done for a card in that " +
        "step (e.g. what 'analyze' or 'implement' involves). Identify the column by display name or id.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "column": { "type": "string", "description": "Column display name (e.g. 'implement') or directory name (e.g. '30-implement')." }
      },
      "required": ["column"],
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
        if (!ToolArguments.TryGetString(arguments, "column", out var columnRef, out var argError))
            return ToolResult.Error(argError);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var column = snapshot.FindColumn(columnRef);
        if (column is null)
            return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

        if (string.IsNullOrWhiteSpace(column.Instructions))
            return ToolResult.Ok($"Column '{column.DisplayName}' has no step instructions.");

        return ToolResult.Ok($"Instructions for '{column.DisplayName}':\n\n{column.Instructions}");
    }
}
