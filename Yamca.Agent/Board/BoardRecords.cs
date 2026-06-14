namespace Yamca.Agent.Board;

/// <summary>The persisted shape of a board column in the VestPocket store, keyed
/// <c>/board/column/{Id}</c>. <see cref="Id"/> is a generated, opaque token (not derived from the
/// name), so dropping and re-adding a column yields a distinct record — a stale id unambiguously
/// refers to a dead column rather than a quickly re-added one. <see cref="Order"/> drives the
/// column's position; <see cref="Instructions"/> (non-blank ⇒ work step) replaces the old
/// per-column <c>instructions.md</c>.</summary>
public sealed record ColumnRecord(string Id, int Order, string DisplayName, string? Instructions);

/// <summary>A single subtask owned by a <see cref="CardRecord"/> — first-class child state of the
/// card aggregate (text plus its done flag), not a markdown line re-parsed on each read.</summary>
public sealed record SubtaskState(string Text, bool Done);

/// <summary>The persisted shape of a board card in the VestPocket store, keyed
/// <c>/board/card/{Id}</c>. The card is an aggregate root: it owns its <see cref="Subtasks"/>
/// inline. <see cref="Id"/> is a monotonic integer display id starting at 1 (e.g. <c>7</c>), handed
/// out by the <see cref="CardCounter"/> and never reused. A card's column membership is the
/// <see cref="ColumnId"/> field — a move rewrites that field rather than relocating a file.
/// <see cref="Body"/> is the prose description; <see cref="Subtasks"/> is the authoritative checklist.</summary>
public sealed record CardRecord(
    int Id,
    string Title,
    string? Branch,
    CardPriority Priority,
    string ColumnId,
    string Body,
    IReadOnlyList<SubtaskState> Subtasks);

/// <summary>The board's monotonic card-id counter, persisted at <c>/board/card/last-id</c>.
/// <see cref="LastId"/> is the most recently assigned card id; the next card takes
/// <c>LastId + 1</c>. It is only ever advanced as cards are created — deleting a card never frees
/// its id — so a given id refers to at most one card. The sole exception is a board wipe-reinit,
/// which clears the counter so numbering starts fresh at 1. Stored under the card key prefix but as
/// a distinct type, so the card scan (which filters on <see cref="CardRecord"/>) skips it.</summary>
public sealed record CardCounter(int LastId);
