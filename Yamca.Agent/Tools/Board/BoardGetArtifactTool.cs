using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Returns one named artifact's content from a card (or, with no <c>kind</c>, the list of the
/// card's available artifact kinds). The read counterpart to <see cref="BoardSetArtifactTool"/>.</summary>
public sealed class BoardGetArtifactTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardGetArtifactTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_get_artifact";

    public string Description =>
        "Read an artifact previously attached to a board card with board_set_artifact (a plan, analysis, " +
        "verification notes, a log). Pass 'card' and the 'kind' to fetch. Omit 'kind' to list the kinds of " +
        "artifact the card currently has.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card": { "type": "string", "description": "Card id (e.g. '7')." },
        "kind": { "type": "string", "description": "Artifact label to fetch (e.g. 'plan'). Omit to list available kinds." }
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
        if (!ToolArguments.TryGetString(arguments, "card", out var cardRef, out var cardErr))
            return ToolResult.Error(cardErr);

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        // 'kind' is optional: with it, return that artifact's content; without it, list what's available.
        var hasKind = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("kind", out var kindProp)
            && kindProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(kindProp.GetString());

        if (!hasKind)
        {
            if (card.Artifacts.Count == 0)
                return ToolResult.Ok($"Card #{card.Id} has no artifacts.");
            return ToolResult.Ok(
                $"Card #{card.Id} artifacts: {string.Join(", ", card.Artifacts.Select(a => a.Kind))}. " +
                "Pass 'kind' to fetch one.");
        }

        var kind = arguments.GetProperty("kind").GetString()!;
        var artifact = card.FindArtifact(kind);
        if (artifact is null)
        {
            var available = card.Artifacts.Count == 0
                ? "it has none"
                : $"available: {string.Join(", ", card.Artifacts.Select(a => a.Kind))}";
            return ToolResult.Error($"Card #{card.Id} has no '{kind.Trim()}' artifact ({available}).");
        }

        return ToolResult.Ok($"Card #{card.Id} artifact '{artifact.Kind}':\n\n{artifact.Content}");
    }
}
