using System.Text;
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
        "checklist) so it can be read or edited. Identify the card by its id (e.g. '7'). The card body is just the " +
        "request/abstract; its step outputs are stored as separate artifacts — their kinds are listed at the end, " +
        "and you can inline specific ones by passing 'artifacts', or fetch one later with board_get_artifact.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":      { "type": "string", "description": "Card id (e.g. '7')." },
        "artifacts": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Optional artifact kinds to inline with the card (e.g. ['plan']). Omit to just list the available kinds."
        }
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

        var sb = new StringBuilder();
        sb.Append("Card #").Append(card.Id).Append(" in column '").Append(columnName).Append("':\n\n");
        sb.Append(CardMarkdown.Render(card));

        // Artifacts live off the body. Inline the kinds the caller named; for the rest, just advertise
        // their availability so the body stays uncluttered and large logs aren't pulled in unasked-for.
        if (card.Artifacts.Count > 0)
        {
            var requested = RequestedKinds(arguments);
            var inlined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in requested)
            {
                if (card.FindArtifact(kind) is { } a && inlined.Add(a.Kind))
                    sb.Append("\n--- artifact: ").Append(a.Kind).Append(" ---\n").Append(a.Content).Append('\n');
            }

            var remaining = card.Artifacts.Where(a => !inlined.Contains(a.Kind)).Select(a => a.Kind).ToList();
            if (remaining.Count > 0)
                sb.Append("\nArtifacts (fetch with board_get_artifact): ").Append(string.Join(", ", remaining)).Append('\n');
        }

        return ToolResult.Ok(sb.ToString());
    }

    private static IReadOnlyList<string> RequestedKinds(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("artifacts", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var kinds = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                kinds.Add(s);
        return kinds;
    }
}
