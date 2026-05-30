using System.Text;
using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Restores the dev board to the default column layout and instructions.
/// Cards in default columns stay in place; cards in non-default columns move to the
/// initial (idea) column. Pass <c>wipe: true</c> to delete all cards instead.</summary>
public sealed class BoardReinitTool : ITool
{
    private readonly BoardWorktree _boardWorktree;

    public BoardReinitTool(BoardWorktree boardWorktree)
    {
        _boardWorktree = boardWorktree;
    }

    public string Name => "board_reinit";

    public string Description =>
        "Restore the dev board to the default column layout (idea, analyze, implement, verify, done) " +
        "and reset all instructions.md files to their defaults. Cards already in a default column stay " +
        "in place; cards in unknown columns are moved to the idea column. Pass wipe:true to delete all " +
        "cards instead. Use this when the board structure has been accidentally corrupted or removed.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "wipe": {
          "type": "boolean",
          "description": "Delete all cards instead of moving orphaned cards to idea. Default: false."
        }
      },
      "additionalProperties": false
    }
    """;

    public bool SupportsWorkspaceRestriction => false;

    public PermissionLevel DefaultPermission => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        bool wipe = false;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("wipe", out var wipeProp)
            && wipeProp.ValueKind == JsonValueKind.True)
        {
            wipe = true;
        }

        var r = await _boardWorktree.ReinitAsync(wipe, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("Board reinitialized to default layout.");
        sb.AppendLine($"  Columns created:       {r.ColumnsCreated}");
        sb.AppendLine($"  Instructions restored: {r.InstructionsRestored}");
        sb.AppendLine($"  Cards preserved:       {r.CardsPreserved}");
        sb.AppendLine($"  Cards moved to idea:   {r.CardsMoved}");
        if (r.CardsWiped > 0) sb.AppendLine($"  Cards wiped:           {r.CardsWiped}");

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}
