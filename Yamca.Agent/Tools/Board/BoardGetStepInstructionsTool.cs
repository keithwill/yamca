using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns a column's <c>instructions.md</c> — the work normally done in that step.</summary>
public sealed class BoardGetStepInstructionsTool : ITool
{
    private readonly BoardService _board;

    public BoardGetStepInstructionsTool(BoardService board) => _board = board;

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

    public bool SupportsWorkspaceRestriction => true;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetString(arguments, "column", out var columnRef, out var argError))
            return Task.FromResult(ToolResult.Error(argError));

        var snapshot = _board.Read(context.Workspace.RootPath);
        var column = snapshot.FindColumn(columnRef);
        if (column is null)
            return Task.FromResult(ToolResult.Error($"Unknown column '{columnRef}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}."));

        var instructions = _board.ReadInstructions(context.Workspace.RootPath, column.DirectoryName);
        if (string.IsNullOrWhiteSpace(instructions))
            return Task.FromResult(ToolResult.Ok($"Column '{column.DisplayName}' has no instructions.md."));

        return Task.FromResult(ToolResult.Ok($"Instructions for '{column.DisplayName}':\n\n{instructions}"));
    }
}
