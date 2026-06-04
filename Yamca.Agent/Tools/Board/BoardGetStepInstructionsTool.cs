using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns a column's <c>instructions.md</c> — the work normally done in that step.</summary>
public sealed class BoardGetStepInstructionsTool : ITool
{
    private readonly BoardService _board;
    private readonly BoardStore _boardStore;

    public BoardGetStepInstructionsTool(BoardService board, BoardStore boardStore)
    {
        _board = board;
        _boardStore = boardStore;
    }

    public string Name => "board_get_step_instructions";

    public string Description =>
        "Return the instructions.md for a board column — guidance on the work normally done for a card in that " +
        "step (e.g. what 'analyze' or 'implement' involves). Identify the column by display name or directory name.";

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

        var boardRoot = await _boardStore.EnsureAsync(cancellationToken);
        var snapshot = _board.Read(boardRoot);
        var column = snapshot.FindColumn(columnRef);
        if (column is null)
            return ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");

        var instructions = _board.ReadInstructions(boardRoot, column.DirectoryName);
        if (string.IsNullOrWhiteSpace(instructions))
            return ToolResult.Ok($"Column '{column.DisplayName}' has no instructions.md.");

        return ToolResult.Ok($"Instructions for '{column.DisplayName}':\n\n{instructions}");
    }
}
