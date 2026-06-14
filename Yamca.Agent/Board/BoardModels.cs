namespace Yamca.Agent.Board;

/// <summary>A single task belonging to a card — a child checklist item kept off the card
/// <see cref="BoardCard.Body"/>. <see cref="Id"/> is the card-local integer id (starts at 1, never
/// reused within the card) the task tools and UI use to address it.</summary>
public sealed record TaskItem(int Id, string Text, bool Done);

/// <summary>A named step-output artifact belonging to a card — read-model projection of an
/// <see cref="ArtifactState"/>. <see cref="Kind"/> is the caller-chosen label, <see cref="Content"/>
/// the markdown/plain-text body, <see cref="UpdatedAt"/> the last write time. Artifacts hold plans,
/// analysis, verification notes, or logs that belong off the card's <see cref="BoardCard.Body"/>.</summary>
public sealed record CardArtifact(string Kind, string Content, DateTimeOffset UpdatedAt);

/// <summary>Card importance level. <see cref="Normal"/> is the default. Cards are sorted
/// high → normal → low within each column.</summary>
public enum CardPriority { Low = -1, Normal = 0, High = 1 }

/// <summary>A board card. <see cref="Id"/> is the canonical integer display id (starts at 1, never
/// reused); <see cref="ColumnId"/> names the column the card currently lives in (a move rewrites this
/// field); <see cref="Branch"/> is the git branch bound to the card across steps, if any. The
/// read-model projection of a <see cref="CardRecord"/>. <see cref="Artifacts"/> is an init property
/// (defaulting to empty) rather than a constructor parameter so the existing positional call sites
/// stay unchanged.</summary>
public sealed record BoardCard(
    int Id,
    string Title,
    string? Branch,
    string ColumnId,
    string Body,
    IReadOnlyList<TaskItem> Tasks,
    CardPriority Priority = CardPriority.Normal)
{
    /// <summary>The card's named step-output artifacts (plan, notes, logs), kept off the
    /// <see cref="Body"/>. Empty when the card has none.</summary>
    public IReadOnlyList<CardArtifact> Artifacts { get; init; } = Array.Empty<CardArtifact>();

    /// <summary>Locate an artifact by its kind (case-insensitive). Returns null when none matches.</summary>
    public CardArtifact? FindArtifact(string kind)
        => Artifacts.FirstOrDefault(a => string.Equals(a.Kind, (kind ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
}

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

    /// <summary>Locate a card by its integer id. Returns null when nothing matches.</summary>
    public BoardCard? FindCard(int id) => AllCards.FirstOrDefault(c => c.Id == id);

    /// <summary>Locate a card from a textual id reference (e.g. a tool argument). Tolerates leading
    /// zeros and surrounding whitespace so "7" and "0007" both match card 7. Returns null when the
    /// reference is blank, non-numeric, or matches no card.</summary>
    public BoardCard? FindCard(string id)
        => int.TryParse((id ?? string.Empty).Trim(), out var n) ? FindCard(n) : null;

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
