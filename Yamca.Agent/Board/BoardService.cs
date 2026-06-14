using System.Globalization;
using System.Text;

namespace Yamca.Agent.Board;

/// <summary>
/// Pure, stateless helpers for the dev board: the default column layout, card ordering, subtask
/// progress, and branch-name derivation. Board persistence lives in <see cref="BoardStore"/>; the
/// agent-facing markdown ⇄ card mapping lives in <see cref="CardMarkdown"/>.
/// </summary>
public sealed class BoardService
{
    /// <summary>The default column layout used to seed a fresh board: an ordered set of columns, each
    /// with optional instructions (non-blank ⇒ a work step run in chat; null ⇒ a resting column such as
    /// idea or done). Shared by the board bootstrap so there is a single definition of the starting
    /// board.</summary>
    public static readonly IReadOnlyList<(int Order, string DisplayName, string? Instructions)> DefaultColumns =
        new (int, string, string?)[]
    {
        (10, "idea", null),
        (20, "analyze", "# Analyze\n\nInvestigate the codebase, identify the files and patterns involved, and write a concrete implementation plan into the card with board_update_card. Break the work into a `- [ ]` subtask checklist where useful. When the plan is ready, move the card to the next column with board_move_card.\n"),
        (30, "implement", "# Implement\n\nDo the work described in the card. Tick subtasks as you complete them. When the implementation is done, commit your code changes on this branch, then move the card to the next column with board_move_card.\n"),
        (40, "verify", "# Verify\n\nBuild, run tests, and confirm the change works end to end. Fix anything that fails. Note verification results on the card with board_update_card, commit any fixes on this branch, then move the card to the next column with board_move_card.\n"),
        (50, "done", null),
    };

    /// <summary>Card order within a column: high → normal → low priority, then by id ascending
    /// (oldest first, since ids are handed out monotonically). Public so the orchestrator's dispatch
    /// sort matches the board's display order exactly.</summary>
    public static int CompareCards(BoardCard a, BoardCard b)
    {
        var pc = b.Priority.CompareTo(a.Priority); // descending: high first
        if (pc != 0) return pc;
        return a.Id.CompareTo(b.Id);
    }

    /// <summary>(done, total) checklist counts for a card's subtasks.</summary>
    public static (int done, int total) SubtaskProgress(IReadOnlyList<SubtaskItem> subtasks)
        => (subtasks.Count(s => s.Done), subtasks.Count);

    /// <summary>The default git branch name for a card: an id-prefixed slug of its title
    /// (e.g. <c>1-test-card</c>). Falls back to the bare id when the title slugs to nothing. Used to
    /// pre-fill the branch field before a card is bound to a branch.</summary>
    public static string PresumptiveBranch(int id, string title)
    {
        var idText = id.ToString(CultureInfo.InvariantCulture);
        var slug = Slugify(title);
        return slug.Length == 0 ? idText : $"{idText}-{slug}";
    }

    internal static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var sb = new StringBuilder(title.Length);
        var lastDash = false;
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
