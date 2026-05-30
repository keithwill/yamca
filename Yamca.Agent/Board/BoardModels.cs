namespace Yamca.Agent.Board;

/// <summary>A single subtask checklist item parsed from a card body
/// (a GitHub-style <c>- [ ]</c> / <c>- [x]</c> line).</summary>
public sealed record SubtaskItem(string Text, bool Done);

/// <summary>A board card: one markdown file living in its current column's directory.
/// <see cref="Id"/> is canonical (frontmatter <c>id</c> or the filename's leading digits);
/// <see cref="Branch"/> is the git branch bound to the card across steps, if any.</summary>
public sealed record BoardCard(
    string Id,
    string Title,
    string? Branch,
    string FileName,
    string ColumnDirectory,
    string AbsolutePath,
    string Body,
    IReadOnlyList<SubtaskItem> Subtasks);

/// <summary>A board column, materialized from a numeric-prefixed directory
/// (e.g. <c>30-implement</c>). <see cref="Order"/> is the numeric prefix and
/// <see cref="DisplayName"/> is the remainder.</summary>
public sealed record BoardColumn(
    string DirectoryName,
    int Order,
    string DisplayName,
    string AbsolutePath,
    IReadOnlyList<BoardCard> Cards);

/// <summary>Immutable view of the whole board at a point in time.</summary>
public sealed record BoardSnapshot(IReadOnlyList<BoardColumn> Columns)
{
    public static BoardSnapshot Empty { get; } = new(Array.Empty<BoardColumn>());

    public IEnumerable<BoardCard> AllCards => Columns.SelectMany(c => c.Cards);

    /// <summary>Locate a card by id, file name (with or without <c>.md</c>), or absolute path.
    /// Returns null when nothing matches; throws nothing.</summary>
    public BoardCard? FindCard(string idOrPath)
    {
        if (string.IsNullOrWhiteSpace(idOrPath)) return null;
        var needle = idOrPath.Trim();

        foreach (var card in AllCards)
            if (string.Equals(card.Id, needle, StringComparison.OrdinalIgnoreCase))
                return card;

        // Numeric match so "7" finds a card whose id is "0007" (and vice versa).
        if (int.TryParse(needle, out var wanted))
            foreach (var card in AllCards)
                if (int.TryParse(card.Id, out var cardNum) && cardNum == wanted)
                    return card;

        foreach (var card in AllCards)
            if (string.Equals(card.AbsolutePath, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.FileName, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(card.FileName), needle, StringComparison.OrdinalIgnoreCase))
                return card;

        return null;
    }

    /// <summary>Locate a column by display name or directory name (case-insensitive).</summary>
    public BoardColumn? FindColumn(string nameOrDir)
    {
        if (string.IsNullOrWhiteSpace(nameOrDir)) return null;
        var needle = nameOrDir.Trim();
        return Columns.FirstOrDefault(c =>
            string.Equals(c.DisplayName, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.DirectoryName, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The column immediately after <paramref name="column"/> in board order, or null if it is the last.</summary>
    public BoardColumn? NextColumn(BoardColumn column)
    {
        var idx = Columns.ToList().FindIndex(c => c.DirectoryName == column.DirectoryName);
        return idx >= 0 && idx + 1 < Columns.Count ? Columns[idx + 1] : null;
    }
}

/// <summary>Summary of what <c>board reinit</c> changed.</summary>
public sealed record ReinitResult(
    int ColumnsCreated,
    int InstructionsRestored,
    int CardsPreserved,
    int CardsMoved,
    int CardsWiped);
