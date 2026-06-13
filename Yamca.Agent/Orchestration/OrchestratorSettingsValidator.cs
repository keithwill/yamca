using Yamca.Agent.Board;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Orchestration;

/// <summary>Result of a per-tick dispatch-config validation. Errors block dispatch (the
/// orchestrator keeps reconciling but starts no new runs); warnings are surfaced to the
/// operator but do not block.</summary>
public sealed record OrchestratorValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Pure validation of orchestrator dispatch configuration against the current
/// board and endpoint state. Run before every dispatch tick (Symphony's preflight rule:
/// validation failure skips dispatch but never stops reconciliation).</summary>
public static class OrchestratorSettingsValidator
{
    public static OrchestratorValidationResult Validate(
        OrchestratorSettings settings,
        EndpointsSettings endpoints,
        BoardSnapshot board)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(board);

        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings.EnabledColumns.Count == 0)
            errors.Add("No board columns are enabled for orchestration.");

        foreach (var columnId in settings.EnabledColumns)
        {
            var column = board.Columns.FirstOrDefault(c =>
                string.Equals(c.Id, columnId, StringComparison.OrdinalIgnoreCase));
            if (column is null)
                errors.Add($"An enabled column no longer exists on the board.");
            else if (string.IsNullOrWhiteSpace(column.Instructions))
                errors.Add($"Enabled column '{column.DisplayName}' has no step instructions, so there is nothing to run.");
        }

        if (endpoints.Items.Count == 0)
            errors.Add("No endpoints are configured.");
        else if (settings.EndpointId is Guid id && endpoints.FindById(id) is null)
            errors.Add("The configured orchestrator endpoint no longer exists; pick another or use the default.");

        if (settings.AllowedTools.Count == 0)
            errors.Add("The orchestrator's allowed-tools list is empty.");
        else if (!settings.AllowedTools.Contains("board_move_card", StringComparer.Ordinal))
            warnings.Add("board_move_card is not in the allowed tools — agents cannot signal step completion by moving their card, so every run will exhaust its turns.");

        return new OrchestratorValidationResult(errors, warnings);
    }
}
