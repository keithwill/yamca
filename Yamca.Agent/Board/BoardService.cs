using System.Globalization;
using System.Text;

namespace Yamca.Agent.Board;

/// <summary>
/// Pure, stateless helpers for the dev board: the default column layout, card ordering, task
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
        (20, "plan", "Get the card and board columns.\n\nInvestigate the codebase, identify the files and patterns involved, and create a concrete plan for the card. The plan should organize the work by the remaining dev board columns. Save the plan as a 'plan' artifact on the card.\n\nUse the plan to create a list of tasks to track the completion of the work of the plan and add them to the card. Order and prefix tasks by the board column that is expected to complete that task.\n\nWhen the plan and tasks are ready, move the card to the next board column.\n"),
        (30, "implement", "Get the card, making sure to also grab the 'plan' artifact.\n\nDo the implementation work described in the card's plan, marking tasks on the card as complete as the work of each task is done.\n\nDecide if any verification tasks should be done during implementation for cohesion. For example, building the project and adding unit tests and verifying their results can be done together with code changes.\n\nWhen the implementation work is done, commit your code changes to the card's branch with an appropriate commit message, then move the card to the next column.\n"),
        (40, "verify", "Get the card, making sure to grab the 'plan' artifact in the same call.\n\nLook for any verification tasks that haven't been completed and work through those tasks, marking each task complete as you go.\n\nReview the commits made in the code branch and consider if additional verification steps are warranted based on how the card was implemented.\n\nAlso compare the commits against the card's plan and consider if any functionality was left incomplete.\n\nIf any fixes have to be applied, commit the changes with an appropriate message to the card's code branch.\n\nVerification tasks that can't be completed by the LLM due to missing tools can be updated to indicate that they require user verification instead. As an example, testing UI behavior of a web application when no browser tools are available. Tasks marked as for user verification should not be stop this step from being considered complete.\n\nIf the card has been verified, advance it to the next step in the dev board. If it shouldn't be considered verified, add tasks to the card indicating what is incomplete and move the card to the previous column.\n"),
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

    /// <summary>(done, total) checklist counts for a card's tasks.</summary>
    public static (int done, int total) TaskProgress(IReadOnlyList<TaskItem> tasks)
        => (tasks.Count(t => t.Done), tasks.Count);

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
