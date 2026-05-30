using System.Text;
using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Lists the dev board: each column and the cards in it, with subtask progress.</summary>
public sealed class BoardListTool : ITool
{
    private readonly BoardService _board;
    private readonly BoardWorktree _boardWorktree;

    public BoardListTool(BoardService board, BoardWorktree boardWorktree)
    {
        _board = board;
        _boardWorktree = boardWorktree;
    }

    public string Name => "board_list";

    public string Description =>
        "List the dev board under .yamca/board: every column (idea, analyze, …) and the cards currently in it, " +
        "with subtask progress. Optionally pass 'column' to list a single column. Cards are markdown files; a card's " +
        "column is determined solely by which column directory it lives in.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "column": { "type": "string", "description": "Optional: restrict output to one column (display name or directory name)." }
      },
      "additionalProperties": false
    }
    """;

    // The board is a worktree of the yamca-board orphan branch, resolved from the repository root
    // (which may sit above the session's sandbox root). Board tools are never workspace-restricted.
    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Allow;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var boardRoot = await _boardWorktree.EnsureAsync(cancellationToken);
        var snapshot = _board.Read(boardRoot);
        if (snapshot.Columns.Count == 0)
            return ToolResult.Ok("The board is empty or not initialized (no .yamca/board directory with NN-name columns).");

        string? only = null;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("column", out var col) && col.ValueKind == JsonValueKind.String)
        {
            only = col.GetString();
            if (!string.IsNullOrWhiteSpace(only) && snapshot.FindColumn(only) is null)
                return ToolResult.Error($"Unknown column '{only}'. Valid columns: {string.Join(", ", snapshot.Columns.Select(c => c.DisplayName))}.");
        }

        var sb = new StringBuilder();
        foreach (var column in snapshot.Columns)
        {
            if (!string.IsNullOrWhiteSpace(only)
                && !string.Equals(column.DisplayName, only, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(column.DirectoryName, only, StringComparison.OrdinalIgnoreCase))
                continue;

            sb.Append("## ").Append(column.DisplayName).Append(" (").Append(column.Cards.Count).AppendLine(")");
            if (column.Cards.Count == 0)
            {
                sb.AppendLine("  (no cards)");
            }
            else
            {
                foreach (var card in column.Cards)
                {
                    var (done, total) = (card.Subtasks.Count(s => s.Done), card.Subtasks.Count);
                    sb.Append("  #").Append(card.Id).Append(' ').Append(card.Title);
                    if (total > 0) sb.Append("  [").Append(done).Append('/').Append(total).Append(']');
                    if (!string.IsNullOrWhiteSpace(card.Branch)) sb.Append("  (branch: ").Append(card.Branch).Append(')');
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}
