using System.Text.Json;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;

namespace Yamca.Agent.Tools.Board;

/// <summary>Creates or replaces a named artifact on a card — the place to stash a step's durable
/// output (a plan, analysis, verification notes, a build log) so it does not bloat the card body.</summary>
public sealed class BoardSetArtifactTool : ITool
{
    private readonly BoardStore _boardStore;

    public BoardSetArtifactTool(BoardStore boardStore)
    {
        _boardStore = boardStore;
    }

    public string Name => "board_set_artifact";

    public string Description =>
        "Attach a named artifact to a board card — the durable output of a step, stored separately from the card " +
        "body so the body stays the original request/abstract. Use this (not board_update_card) for an " +
        "implementation plan, analysis, verification notes, or a captured log. 'kind' is a short label " +
        "(e.g. 'plan', 'analysis', 'verification', 'build-log'); setting the same kind again replaces it. Pass empty " +
        "'content' to delete that artifact. Retrieve artifacts later with board_get_artifact.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "card":    { "type": "string", "description": "Card id (e.g. '7')." },
        "kind":    { "type": "string", "description": "Artifact label (e.g. 'plan', 'analysis', 'verification', 'build-log')." },
        "content": { "type": "string", "description": "The artifact's markdown/text content. Empty to delete the artifact." }
      },
      "required": ["card", "kind", "content"],
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
        if (!ToolArguments.TryGetString(arguments, "kind", out var kind, out var kindErr))
            return ToolResult.Error(kindErr);
        if (!ToolArguments.TryGetString(arguments, "content", out var content, out var contentErr))
            return ToolResult.Error(contentErr);

        if (string.IsNullOrWhiteSpace(kind))
            return ToolResult.Error("Artifact 'kind' must be a non-empty label.");

        var snapshot = await _boardStore.ReadAsync(cancellationToken);
        var card = snapshot.FindCard(cardRef);
        if (card is null)
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        var removing = string.IsNullOrWhiteSpace(content);
        if (removing && card.FindArtifact(kind) is null)
            return ToolResult.Ok($"Card #{card.Id} has no '{kind.Trim()}' artifact to remove.");

        if (!await _boardStore.SetArtifactAsync(card.Id, kind, content, cancellationToken))
            return ToolResult.Error($"No card matching '{cardRef}' on the board.");

        return ToolResult.Ok(removing
            ? $"Removed the '{kind.Trim()}' artifact from card #{card.Id}."
            : $"Saved the '{kind.Trim()}' artifact on card #{card.Id}.");
    }
}
