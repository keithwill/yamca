using System.Text;

namespace Yamca.Agent.Board;

/// <summary>The agent-facing boundary between a structured <see cref="BoardCard"/> aggregate and the
/// single markdown blob the board tools exchange with the model. <see cref="Render"/> projects a card
/// to <c>frontmatter + body</c>; <see cref="Parse"/> splits a submitted blob back into fields. The
/// body is the card's prose description (the feature/issue/user story) only — a card's child tasks are
/// a separate structured collection edited through the task tools, not parsed out of this markdown.</summary>
public static class CardMarkdown
{
    /// <summary>The fields parsed from a submitted card blob. A null <see cref="Title"/>,
    /// <see cref="Branch"/>, or <see cref="Priority"/> means the frontmatter omitted that field and the
    /// caller should keep the card's existing value.</summary>
    public readonly record struct ParsedCard(
        string? Title,
        string? Branch,
        CardPriority? Priority,
        string Body);

    /// <summary>Render a card as the markdown the agent sees: a frontmatter block (id, title, branch,
    /// priority) followed by the prose body. Tasks are not part of this blob — they are listed and
    /// edited separately through the task tools.</summary>
    public static string Render(BoardCard card)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(card.Id).Append('\n');
        sb.Append("title: \"").Append((card.Title ?? string.Empty).Replace("\"", "'")).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(card.Branch))
            sb.Append("branch: ").Append(card.Branch).Append('\n');
        sb.Append("priority: ").Append(card.Priority.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("---\n\n");

        var body = (card.Body ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (body.Length > 0) sb.Append(body).Append('\n');

        return sb.ToString();
    }

    /// <summary>Parse a submitted card blob into fields. Frontmatter scalars become title/branch/priority
    /// (the <c>id</c> is ignored — card identity is fixed by the tool argument) and the remaining text is
    /// the body verbatim. Any checklist-looking lines stay in the body; tasks are edited via the task
    /// tools, not authored through this markdown.</summary>
    public static ParsedCard Parse(string content)
    {
        var (fm, rawBody) = SplitFrontmatter(content ?? string.Empty);

        string? title = fm.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;
        string? branch = fm.TryGetValue("branch", out var b) && !string.IsNullOrWhiteSpace(b) ? b : null;

        CardPriority? priority = fm.TryGetValue("priority", out var p) ? p.ToLowerInvariant() switch
        {
            "high" => CardPriority.High,
            "low" => CardPriority.Low,
            "normal" => CardPriority.Normal,
            _ => null,
        } : null;

        return new ParsedCard(title, branch, priority, rawBody.Replace("\r\n", "\n").Trim());
    }

    // Splits leading YAML-ish frontmatter (between '---' fences) into a flat key/value map and returns
    // the remaining body. Hand-rolled to avoid a YAML dependency; only scalar key: value lines are read.
    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (empty, text);

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (empty, text);

        var block = normalized.Substring(4, end - 4);
        var afterFence = end + "\n---".Length;
        var nl = normalized.IndexOf('\n', afterFence);
        var body = nl < 0 ? string.Empty : normalized[(nl + 1)..];

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0) map[key] = value;
        }
        return (map, body);
    }
}
