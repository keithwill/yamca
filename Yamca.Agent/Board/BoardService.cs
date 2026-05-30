using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yamca.Agent.Board;

/// <summary>
/// Reads and parses the git-native dev board under <c>.yamca/board</c>. Pure filesystem +
/// parsing — no git, no mutation. Columns are numeric-prefixed subdirectories
/// (<c>10-idea</c>, <c>20-analyze</c>, …); a card is a markdown file living in its current
/// column's directory. Stateless and reentrant; the workspace root is supplied per call so
/// the same instance serves both the root workspace and branch worktrees.
/// </summary>
public sealed partial class BoardService
{
    public const string BoardRelativePath = ".yamca/board";
    public const string InstructionsFileName = "instructions.md";

    /// <summary>The default column layout used to seed a fresh board: numeric-prefixed directories,
    /// each with an <c>instructions.md</c> (empty for resting columns). A column with non-blank
    /// instructions is a work step run in chat; idea is a scratchpad and done is terminal, so both
    /// rest. Shared by the orphan-branch bootstrap (<c>BoardWorktree</c>) and the UI initialize path
    /// so there is a single definition of the starting board.</summary>
    public static readonly IReadOnlyList<(string Dir, string? Instructions)> DefaultColumns = new (string, string?)[]
    {
        ("10-idea", null),
        ("20-analyze", "# Analyze\n\nInvestigate the codebase, identify the files and patterns involved, and write a concrete implementation plan into the card. Break the work into a `- [ ]` subtask checklist where useful.\n"),
        ("30-implement", "# Implement\n\nDo the work described in the card. Tick subtasks as you complete them. When the implementation is done, commit your code changes on this branch, then move the card to the next column with board_move_card — the board is tracked separately and the move is committed for you.\n"),
        ("40-verify", "# Verify\n\nBuild, run tests, and confirm the change works end to end. Fix anything that fails. Note verification results on the card before moving it on.\n"),
        ("50-done", null),
    };

    [GeneratedRegex(@"^(\d+)-(.+)$")]
    private static partial Regex ColumnDirRegex();

    [GeneratedRegex(@"^\s*-\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex SubtaskRegex();

    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    /// <summary>Absolute path to the board directory. Under the orphan-branch layout the board
    /// worktree's root <em>is</em> the columns directory, so callers pass the board worktree path
    /// (from <c>BoardWorktree.EnsureAsync</c>) and this returns it unchanged.</summary>
    public static string BoardDirectory(string boardRoot) => boardRoot;

    /// <summary>Read the whole board. Returns <see cref="BoardSnapshot.Empty"/> when the
    /// board directory does not exist. Never throws for malformed cards.</summary>
    public BoardSnapshot Read(string boardRoot)
    {
        var boardDir = BoardDirectory(boardRoot);
        if (!Directory.Exists(boardDir)) return BoardSnapshot.Empty;

        var columns = new List<BoardColumn>();
        foreach (var dir in Directory.EnumerateDirectories(boardDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!TryParseColumnDir(dirName, out var order, out var displayName))
                continue;

            var cards = new List<BoardCard>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                if (string.Equals(Path.GetFileName(file), InstructionsFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                cards.Add(ParseCard(dirName, file, text));
            }

            cards.Sort(CompareCards);

            columns.Add(new BoardColumn(dirName, order, displayName, dir, cards));
        }

        columns.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return new BoardSnapshot(columns);
    }

    // Card order within a column: by leading numeric id, then file name (both case-insensitive).
    private static int CompareCards(BoardCard a, BoardCard b)
    {
        var an = LeadingInt(a.Id);
        var bn = LeadingInt(b.Id);
        if (an.HasValue && bn.HasValue && an != bn) return an.Value.CompareTo(bn.Value);
        return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parse a column directory name of the form <c>NN-display-name</c>.</summary>
    public static bool TryParseColumnDir(string dirName, out int order, out string displayName)
    {
        order = 0;
        displayName = string.Empty;
        if (string.IsNullOrEmpty(dirName)) return false;

        var m = ColumnDirRegex().Match(dirName);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out order))
            return false;
        displayName = m.Groups[2].Value;
        return true;
    }

    /// <summary>Parse one card file's text into a <see cref="BoardCard"/>. Never throws.</summary>
    public BoardCard ParseCard(string columnDirName, string absPath, string text)
    {
        var fileName = Path.GetFileName(absPath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var (fm, body) = SplitFrontmatter(text);

        var id = fm.GetValueOrDefault("id");
        if (string.IsNullOrWhiteSpace(id))
            id = LeadingDigits(stem) ?? stem;

        var title = fm.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var heading = HeadingRegex().Match(body);
            title = heading.Success ? heading.Groups[1].Value : stem;
        }

        var branch = fm.GetValueOrDefault("branch");
        if (string.IsNullOrWhiteSpace(branch)) branch = null;

        var subtasks = ParseSubtasks(body);
        return new BoardCard(id.Trim(), title.Trim(), branch?.Trim(), fileName, columnDirName, absPath, body, subtasks);
    }

    /// <summary>(done, total) checklist counts for a card body.</summary>
    public static (int done, int total) SubtaskProgress(string body)
    {
        var done = 0;
        var total = 0;
        foreach (var item in ParseSubtasks(body))
        {
            total++;
            if (item.Done) done++;
        }
        return (done, total);
    }

    /// <summary>Read a column's <c>instructions.md</c>, or null if absent.</summary>
    public string? ReadInstructions(string workspaceRoot, string columnDirName)
    {
        var path = Path.Combine(BoardDirectory(workspaceRoot), columnDirName, InstructionsFileName);
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
    }

    /// <summary>True when a column has *non-blank* instructions, which is what makes it a *work*
    /// step (an agent runs it in chat). A column whose <c>instructions.md</c> is missing or blank is
    /// a *resting* column (idea scratchpad, done, blocked, …) whose cards are simply promoted to the
    /// next column without a chat run. Resting columns still carry an empty <c>instructions.md</c> so
    /// their directory survives in git, which does not track empty directories.</summary>
    public bool HasInstructions(string workspaceRoot, string columnDirName)
        => !string.IsNullOrWhiteSpace(ReadInstructions(workspaceRoot, columnDirName));

    /// <summary>Next free 4-digit card id (max existing numeric id across all columns + 1).</summary>
    public string NextCardId(string workspaceRoot)
    {
        var max = 0;
        foreach (var card in Read(workspaceRoot).AllCards)
            if (LeadingInt(card.Id) is int n && n > max)
                max = n;
        return (max + 1).ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>Return <paramref name="rawText"/> with its frontmatter <c>branch:</c> set to
    /// <paramref name="branch"/>, adding a frontmatter block if none exists. Used to bind a card
    /// to a git branch. Normalizes line endings to LF.</summary>
    public static string WithBranch(string rawText, string branch)
        => WithFrontmatterField(rawText, "branch", branch);

    /// <summary>Return <paramref name="rawText"/> with its frontmatter <c>commit:</c> set to
    /// <paramref name="sha"/>: the last code commit associated with the card, stamped by a board
    /// move so the status change stays linked to the code it corresponds to. Normalizes line
    /// endings to LF.</summary>
    public static string WithCommit(string rawText, string sha)
        => WithFrontmatterField(rawText, "commit", sha);

    // Sets a scalar frontmatter field, replacing an existing line for the key or appending one,
    // and synthesizing a frontmatter block when the text has none. Backs WithBranch / WithCommit.
    private static string WithFrontmatterField(string rawText, string key, string value)
    {
        var normalized = (rawText ?? string.Empty).Replace("\r\n", "\n");

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end >= 0)
            {
                var block = normalized.Substring(4, end - 4);
                var lines = block.Split('\n').ToList();
                var idx = lines.FindIndex(l => l.TrimStart().StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) lines[idx] = $"{key}: {value}";
                else lines.Add($"{key}: {value}");

                var afterFence = normalized.IndexOf('\n', end + "\n---".Length);
                var body = afterFence < 0 ? string.Empty : normalized[(afterFence + 1)..];
                return $"---\n{string.Join('\n', lines)}\n---\n{body}";
            }
        }

        return $"---\n{key}: {value}\n---\n\n{normalized}";
    }

    /// <summary>Build a card file name <c>NNNN-slug.md</c> from an id and title.</summary>
    public static string CardFileName(string id, string title)
    {
        var slug = Slugify(title);
        return slug.Length == 0 ? $"{id}.md" : $"{id}-{slug}.md";
    }

    /// <summary>The default git branch name for a card: an id-prefixed slug of its title
    /// (e.g. <c>0001-test-card</c>), mirroring <see cref="CardFileName"/>. Falls back to the
    /// bare id when the title slugs to nothing. Used to pre-fill the branch field before a
    /// card is bound to a branch.</summary>
    public static string PresumptiveBranch(string id, string title)
    {
        var slug = Slugify(title);
        return slug.Length == 0 ? id : $"{id}-{slug}";
    }

    private static IReadOnlyList<SubtaskItem> ParseSubtasks(string body)
    {
        var items = new List<SubtaskItem>();
        foreach (var raw in body.Split('\n'))
        {
            var m = SubtaskRegex().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var done = m.Groups[1].Value is "x" or "X";
            items.Add(new SubtaskItem(m.Groups[2].Value.Trim(), done));
        }
        return items;
    }

    // Splits leading YAML-ish frontmatter (between '---' fences) into a flat key/value map and
    // returns the remaining body. Hand-rolled to avoid a YAML dependency; only scalar key: value
    // lines are read. Returns an empty map and the whole text as body when no frontmatter present.
    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text is null) return (empty, string.Empty);

        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (empty, text);

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (empty, text);

        var block = normalized.Substring(4, end - 4);
        var afterFence = end + "\n---".Length;
        // skip to end of the closing fence line
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

    private static string Slugify(string title)
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

    private static string? LeadingDigits(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i == 0 ? null : s[..i];
    }

    private static int? LeadingInt(string s)
    {
        var digits = LeadingDigits(s);
        return digits is not null && int.TryParse(digits, out var n) ? n : null;
    }
}
