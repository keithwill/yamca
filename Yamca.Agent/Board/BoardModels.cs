namespace Yamca.Agent.Board;

/// <summary>A single subtask checklist item belonging to a card.</summary>
public sealed record SubtaskItem(string Text, bool Done);

/// <summary>Card importance level. <see cref="Normal"/> is the default. Cards are sorted
/// high → normal → low within each column.</summary>
public enum CardPriority { Low = -1, Normal = 0, High = 1 }

/// <summary>A board card. <see cref="Id"/> is the canonical 4-digit display id; <see cref="ColumnId"/>
/// names the column the card currently lives in (a move rewrites this field); <see cref="Branch"/> is
/// the git branch bound to the card across steps, if any. The read-model projection of a
/// <see cref="CardRecord"/>.</summary>
public sealed record BoardCard(
    string Id,
    string Title,
    string? Branch,
    string ColumnId,
    string Body,
    IReadOnlyList<SubtaskItem> Subtasks,
    CardPriority Priority = CardPriority.Normal);

/// <summary>A board column. <see cref="Id"/> is the generated, opaque column identity;
/// <see cref="Order"/> is its position; <see cref="Instructions"/> (non-blank ⇒ work step) is the
/// guidance an agent runs for a card in this column. The read-model projection of a
/// <see cref="ColumnRecord"/> plus its cards.</summary>
public sealed record BoardColumn(
    string Id,
    int Order,
    string DisplayName,
    string? Instructions,
    IReadOnlyList<BoardCard> Cards);

/// <summary>Immutable view of the whole board at a point in time. Columns are ordered by
/// <see cref="BoardColumn.Order"/>.</summary>
public sealed record BoardSnapshot(IReadOnlyList<BoardColumn> Columns)
{
    public static BoardSnapshot Empty { get; } = new(Array.Empty<BoardColumn>());

    public IEnumerable<BoardCard> AllCards => Columns.SelectMany(c => c.Cards);

    /// <summary>Locate a card by id. Accepts a numeric form so "7" finds a card whose id is "0007"
    /// (and vice versa). Returns null when nothing matches; throws nothing.</summary>
    public BoardCard? FindCard(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var needle = id.Trim();

        foreach (var card in AllCards)
            if (string.Equals(card.Id, needle, StringComparison.OrdinalIgnoreCase))
                return card;

        // Numeric match so "7" finds a card whose id is "0007" (and vice versa).
        if (int.TryParse(needle, out var wanted))
            foreach (var card in AllCards)
                if (int.TryParse(card.Id, out var cardNum) && cardNum == wanted)
                    return card;

        return null;
    }

    /// <summary>Locate a column by its id or display name (case-insensitive).</summary>
    public BoardColumn? FindColumn(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var needle = idOrName.Trim();
        return Columns.FirstOrDefault(c =>
            string.Equals(c.Id, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.DisplayName, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The column immediately after <paramref name="column"/> in board order, or null if it is the last.</summary>
    public BoardColumn? NextColumn(BoardColumn column)
    {
        var idx = Columns.ToList().FindIndex(c => c.Id == column.Id);
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
