using System.Text.Json.Serialization;

namespace Yamca.Agent.Board;

/// <summary>The persisted shape of a board column in the VestPocket store, keyed
/// <c>/board/column/{Id}</c>. <see cref="Id"/> is a generated, opaque token (not derived from the
/// name), so dropping and re-adding a column yields a distinct record — a stale id unambiguously
/// refers to a dead column rather than a quickly re-added one. <see cref="Order"/> drives the
/// column's position; <see cref="Instructions"/> (non-blank ⇒ work step) replaces the old
/// per-column <c>instructions.md</c>.</summary>
public sealed record ColumnRecord(string Id, int Order, string DisplayName, string? Instructions);

/// <summary>A single task owned by a <see cref="CardRecord"/> — first-class child state of the
/// card aggregate (a card-local <see cref="Id"/>, its text, and its done flag), not a markdown line
/// re-parsed on each read. <see cref="Id"/> is handed out by the card's <see cref="CardRecord.LastTaskId"/>
/// counter and never reused within the card. Records persisted before tasks carried ids deserialize
/// with <c>Id = 0</c>; the read projection renumbers such legacy lists positionally.</summary>
public sealed record TaskState(int Id, string Text, bool Done);

/// <summary>A named, free-form output attached to a card — the durable result of a work step kept
/// out of the card's <see cref="CardRecord.Body"/> so the body stays the original ask/abstract.
/// <see cref="Kind"/> is a caller-chosen label (the column instructions decide the vocabulary —
/// e.g. <c>plan</c>, <c>analysis</c>, <c>verification</c>, <c>build-log</c>); <see cref="Content"/>
/// is markdown/plain text; <see cref="UpdatedAt"/> records the last write. Owned inline by the card
/// aggregate, like its tasks, and at most one artifact exists per kind (a re-set replaces it).</summary>
public sealed record ArtifactState(string Kind, string Content, DateTimeOffset UpdatedAt);

/// <summary>The persisted shape of a board card in the VestPocket store, keyed
/// <c>/board/card/{Id}</c>. The card is an aggregate root: it owns its <see cref="Tasks"/> and
/// <see cref="Artifacts"/> inline. <see cref="Id"/> is a monotonic integer display id starting at 1
/// (e.g. <c>7</c>), handed out by the <see cref="CardCounter"/> and never reused. A card's column
/// membership is the <see cref="ColumnId"/> field — a move rewrites that field rather than relocating
/// a file. <see cref="Body"/> is the prose description (the feature/issue/user story); <see cref="Tasks"/>
/// is the card's child checklist, kept off the body; <see cref="Artifacts"/> are named step outputs
/// (plans, notes, logs) stored apart from the body. <see cref="Tasks"/> still persists under the
/// JSON key <c>Subtasks</c> so cards written before the rename keep loading. <see cref="Artifacts"/>
/// is an init property kept off the positional constructor so the existing call sites stay unchanged.
/// It is nullable on purpose: a card persisted before the field existed has no <c>Artifacts</c> key,
/// and System.Text.Json leaves such an omitted property null (it does not honor the field initializer),
/// so every read path coalesces to empty.</summary>
public sealed record CardRecord(
    int Id,
    string Title,
    string? Branch,
    CardPriority Priority,
    string ColumnId,
    string Body,
    [property: JsonPropertyName("Subtasks")] IReadOnlyList<TaskState> Tasks)
{
    public IReadOnlyList<ArtifactState>? Artifacts { get; init; } = Array.Empty<ArtifactState>();

    /// <summary>The card's monotonic task-id counter: the most recently assigned task id, so the next
    /// task takes <c>LastTaskId + 1</c>. Like <see cref="Artifacts"/> it is an init property kept off
    /// the positional constructor; a card persisted before it existed (or before tasks had ids)
    /// deserializes with 0, and the task-add path advances it past any ids already present.</summary>
    public int LastTaskId { get; init; }
}

/// <summary>The board's monotonic card-id counter, persisted at <c>/board/card/last-id</c>.
/// <see cref="LastId"/> is the most recently assigned card id; the next card takes
/// <c>LastId + 1</c>. It is only ever advanced as cards are created — deleting a card never frees
/// its id — so a given id refers to at most one card. The sole exception is a board wipe-reinit,
/// which clears the counter so numbering starts fresh at 1. Stored under the card key prefix but as
/// a distinct type, so the card scan (which filters on <see cref="CardRecord"/>) skips it.</summary>
public sealed record CardCounter(int LastId);
