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

    [GeneratedRegex(@"^(\d+)-(.+)$")]
    private static partial Regex ColumnDirRegex();

    [GeneratedRegex(@"^\s*-\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex SubtaskRegex();

    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    /// <summary>Absolute path to the board directory for a workspace root.</summary>
    public static string BoardDirectory(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".yamca", "board");

    /// <summary>Read the whole board. Returns <see cref="BoardSnapshot.Empty"/> when the
    /// board directory does not exist. Never throws for malformed cards.</summary>
    public BoardSnapshot Read(string workspaceRoot)
    {
        var boardDir = BoardDirectory(workspaceRoot);
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

            cards.Sort(static (a, b) =>
            {
                var an = LeadingInt(a.Id);
                var bn = LeadingInt(b.Id);
                if (an.HasValue && bn.HasValue && an != bn) return an.Value.CompareTo(bn.Value);
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            columns.Add(new BoardColumn(dirName, order, displayName, dir, cards));
        }

        columns.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return new BoardSnapshot(columns);
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
    {
        var normalized = (rawText ?? string.Empty).Replace("\r\n", "\n");

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end >= 0)
            {
                var block = normalized.Substring(4, end - 4);
                var lines = block.Split('\n').ToList();
                var idx = lines.FindIndex(l => l.TrimStart().StartsWith("branch:", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) lines[idx] = $"branch: {branch}";
                else lines.Add($"branch: {branch}");

                var afterFence = normalized.IndexOf('\n', end + "\n---".Length);
                var body = afterFence < 0 ? string.Empty : normalized[(afterFence + 1)..];
                return $"---\n{string.Join('\n', lines)}\n---\n{body}";
            }
        }

        return $"---\nbranch: {branch}\n---\n\n{normalized}";
    }

    /// <summary>Build a card file name <c>NNNN-slug.md</c> from an id and title.</summary>
    public static string CardFileName(string id, string title)
    {
        var slug = Slugify(title);
        return slug.Length == 0 ? $"{id}.md" : $"{id}-{slug}.md";
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
