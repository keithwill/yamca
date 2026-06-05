using System.Text;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Subagents;

/// <summary>The fan-out/reduce engine behind the <c>loop</c> tool. Reuses <see cref="SubagentRunner"/>
/// wholesale for each item (agent lookup, client resolution, curated tools, observability) and adds
/// the three things that justify a first-class feature over N hand-written <c>subagent_run</c> calls:
/// context collapse (one roll-up instead of N transcripts), a hard item cap, and a mechanical
/// aggregation that surfaces failures without an outer model reading prose.
///
/// v1 runs strictly serial. The engine carries a concurrency cap so a future opt-in (gated behind
/// coordinated settings) can raise it, but it is not exposed yet: parallel writes would race — e.g.
/// board id allocation — so serial is the safe default for write-heavy loops.</summary>
public sealed class BatchRunner : IBatchRunner
{
    /// <summary>Upper bound on items per loop — a session-per-item amplifies cost fast.</summary>
    public const int MaxItems = 50;

    private const int MaxItemLabelWidth = 40;
    private const int CompactSummaryChars = 160;
    private const int VerbatimSummaryChars = 600;

    private readonly ISubagentRunner _runner;
    private readonly ISessionSettings _settings;

    // Reserved for a future opt-in; v1 always runs serial (see class summary).
    private readonly int _maxConcurrency = 1;

    public BatchRunner(ISubagentRunner runner, ISessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(settings);
        _runner = runner;
        _settings = settings;
    }

    public async Task<ToolResult> RunAsync(
        string agentName,
        string promptTemplate,
        IReadOnlyList<string> items,
        ToolContext parentContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentContext);

        if (string.IsNullOrWhiteSpace(agentName))
            return ToolResult.Error("A non-empty 'agent' is required to run a loop.");
        if (string.IsNullOrWhiteSpace(promptTemplate))
            return ToolResult.Error("A non-empty 'prompt' is required to run a loop.");
        if (items is null || items.Count == 0)
            return ToolResult.Error("A non-empty 'items' array is required to run a loop.");
        if (items.Count > MaxItems)
            return ToolResult.Error(
                $"A loop can run at most {MaxItems} items at once ({items.Count} requested). " +
                "Narrow the list or split it across calls.");

        // Validate the agent once, up front, so a bad name fails fast instead of N identical errors.
        if (SubagentRegistry.Resolve(_settings.UserSubagents, _settings.ProjectSubagents, agentName) is null)
        {
            var merged = SubagentRegistry.Merge(_settings.UserSubagents, _settings.ProjectSubagents);
            var hint = merged.Count == 0
                ? "No subagents are configured."
                : "Available subagents: " + string.Join(", ", merged.Select(a => a.Name)) + ".";
            return ToolResult.Error($"Unknown subagent '{agentName}'. {hint}");
        }

        // Groups every child run under this loop in the observability pipeline.
        var loopRunId = Guid.NewGuid().ToString("n");
        var results = new List<(string Item, SubagentOutcome Outcome)>(items.Count);

        _ = _maxConcurrency; // serial in v1; the field documents the intended future axis.
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var itemPrompt = BuildItemPrompt(promptTemplate, item);
            var outcome = await _runner
                .RunCoreAsync(agentName, itemPrompt, parentContext, loopRunId, cancellationToken)
                .ConfigureAwait(false);
            results.Add((item, outcome));
        }

        return ToolResult.Ok(Reduce(agentName, items.Count, results, cancellationToken.IsCancellationRequested));
    }

    /// <summary>Join one item onto the prompt. If the prompt carries an item placeholder
    /// (<c>{{item}}</c>, or the single-brace <c>{item}</c> models often reach for), the item is
    /// substituted in place and nothing is appended — so the subagent sees one coherent prompt
    /// rather than the template plus a trailing "Item:" line. Otherwise the item is appended.</summary>
    private static string BuildItemPrompt(string template, string item)
    {
        if (template.Contains("{{item}}", StringComparison.Ordinal))
            return template.Replace("{{item}}", item, StringComparison.Ordinal);
        if (template.Contains("{item}", StringComparison.Ordinal))
            return template.Replace("{item}", item, StringComparison.Ordinal);
        return $"{template}\n\nItem: {item}";
    }

    private static string Reduce(
        string agentName,
        int total,
        IReadOnlyList<(string Item, SubagentOutcome Outcome)> results,
        bool cancelled)
    {
        var success = results.Where(r => r.Outcome.IsSuccess).ToList();
        var followup = results.Where(r => r.Outcome.IsNeedsFollowup).ToList();
        var failed = results.Where(r => r.Outcome.IsFailure).ToList();

        var sb = new StringBuilder();
        sb.Append("Loop over ").Append(total).Append(total == 1 ? " item" : " items")
          .Append(" with agent '").Append(agentName).Append("': ")
          .Append(success.Count).Append(" success, ")
          .Append(followup.Count).Append(" needs_followup, ")
          .Append(failed.Count).Append(" failed.");
        if (cancelled && results.Count < total)
            sb.Append(" Cancelled after ").Append(results.Count).Append(" of ").Append(total).Append('.');
        sb.AppendLine();

        // Failures and follow-ups verbatim (they're the actionable part); successes compact.
        AppendSection(sb, "failed", failed, VerbatimSummaryChars);
        AppendSection(sb, "needs_followup", followup, VerbatimSummaryChars);
        AppendSection(sb, "success", success, CompactSummaryChars);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(
        StringBuilder sb,
        string label,
        IReadOnlyList<(string Item, SubagentOutcome Outcome)> rows,
        int summaryCap)
    {
        if (rows.Count == 0) return;

        sb.Append("  ").Append(label).AppendLine(":");
        var width = Math.Min(rows.Max(r => Label(r.Item).Length), MaxItemLabelWidth);
        foreach (var (item, outcome) in rows)
        {
            sb.Append("    ").Append(Label(item).PadRight(width))
              .Append("  — ").AppendLine(Flatten(outcome.Summary, summaryCap));
        }
    }

    private static string Label(string item)
    {
        var flat = item.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > MaxItemLabelWidth ? flat[..(MaxItemLabelWidth - 1)] + "…" : flat;
    }

    private static string Flatten(string text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no summary)";
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length > cap ? flat[..cap] + "…" : flat;
    }
}
