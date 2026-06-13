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
/// inline. <see cref="Id"/> stays the human 4-digit display id (e.g. <c>0007</c>) so UI and agent
/// references are unchanged. A card's column membership is the <see cref="ColumnId"/> field — a move
/// rewrites that field rather than relocating a file. <see cref="Body"/> is the prose description;
/// <see cref="Subtasks"/> is the authoritative checklist.</summary>
public sealed record CardRecord(
    string Id,
    string Title,
    string? Branch,
    CardPriority Priority,
    string ColumnId,
    string Body,
    IReadOnlyList<SubtaskState> Subtasks);
