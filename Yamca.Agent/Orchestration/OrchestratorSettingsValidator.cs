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
    /// <param name="columnHasInstructions">Whether a column directory has non-blank
    /// <c>instructions.md</c> — i.e. is a work column (see <see cref="BoardService.HasInstructions"/>).</param>
    public static OrchestratorValidationResult Validate(
        OrchestratorSettings settings,
        EndpointsSettings endpoints,
        BoardSnapshot board,
        Func<string, bool> columnHasInstructions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(columnHasInstructions);

        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings.EnabledColumns.Count == 0)
            errors.Add("No board columns are enabled for orchestration.");

        foreach (var dir in settings.EnabledColumns)
        {
            var column = board.Columns.FirstOrDefault(c =>
                string.Equals(c.DirectoryName, dir, StringComparison.OrdinalIgnoreCase));
            if (column is null)
                errors.Add($"Enabled column '{dir}' does not exist on the board.");
            else if (!columnHasInstructions(column.DirectoryName))
                errors.Add($"Enabled column '{dir}' has no step instructions (instructions.md is empty), so there is nothing to run.");
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
