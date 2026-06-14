using System.Globalization;
using VestPocket;
using Yamca.Agent.Storage;

namespace Yamca.Agent.Board;

/// <summary>The dev board's repository over the shared <see cref="YamcaStore"/> (VestPocket). Columns
/// live at keys <c>/board/column/{id}</c> and cards — aggregate roots that own their tasks — at
/// <c>/board/card/{id}</c>. There are no files or directories: a card's column membership is its
/// <see cref="CardRecord.ColumnId"/> field, so a move is a field rewrite.
///
/// Reads are lock-free against VestPocket's in-memory index. Mutations funnel through a process-wide
/// <see cref="SemaphoreSlim"/> so multi-step read-modify-write sequences (and reinit) are atomic against
/// each other; individual VestPocket saves are last-write-wins, so there is no version bookkeeping.</summary>
public sealed class BoardStore
{
    private const string CardPrefix = "/board/card/";
    private const string ColumnPrefix = "/board/column/";

    // The monotonic card-id counter. It lives under the card prefix but holds a CardCounter, not a
    // CardRecord, so ReadCards — which type-filters on CardRecord — skips it during the card scan.
    private const string CounterKey = CardPrefix + "last-id";

    private readonly YamcaStore _yamca;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardStore(YamcaStore yamca) => _yamca = yamca;

    private static string CardKey(int id) => CardPrefix + id.ToString(CultureInfo.InvariantCulture);
    private static string ColumnKey(string id) => ColumnPrefix + id;

    /// <summary>Seed the default column layout if the board has no columns yet. Idempotent.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        if (ReadColumns(store).Count > 0) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReadColumns(store).Count > 0) return;
            await SeedDefaultColumnsAsync(store, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Read the whole board into an immutable snapshot, seeding the default columns first if the
    /// board is empty. Columns are ordered by <see cref="ColumnRecord.Order"/>; cards are grouped by
    /// their <see cref="CardRecord.ColumnId"/> and sorted by <see cref="BoardService.CompareCards"/>.
    /// Cards referencing a missing column are omitted (reinit relocates such orphans).</summary>
    public async Task<BoardSnapshot> ReadAsync(CancellationToken ct)
    {
        await EnsureSeededAsync(ct).ConfigureAwait(false);
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);

        var cardsByColumn = ReadCards(store)
            .Select(ToCard)
            .GroupBy(c => c.ColumnId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var columns = new List<BoardColumn>();
        foreach (var col in ReadColumns(store).OrderBy(c => c.Order))
        {
            var cards = cardsByColumn.GetValueOrDefault(col.Id) ?? new List<BoardCard>();
            cards.Sort(BoardService.CompareCards);
            columns.Add(new BoardColumn(col.Id, col.Order, col.DisplayName, col.Instructions, cards));
        }

        return new BoardSnapshot(columns);
    }

    /// <summary>A preview of the id the next card would take (the counter's last id + 1). The id is
    /// only actually assigned — and the counter advanced — inside <see cref="AddCardAsync"/>, so this
    /// is advisory (e.g. to seed a default branch name).</summary>
    public async Task<int> NextCardIdAsync(CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        return ReadLastId(store) + 1;
    }

    /// <summary>Create a card in <paramref name="columnId"/> from a freeform body (stored verbatim as the
    /// card's description; the card starts with no tasks). Advances the card-id counter and stamps the
    /// card with the new id, persisting both atomically. Returns the new card's id.</summary>
    public async Task<int> AddCardAsync(
        string columnId, string title, string body, string? branch, CardPriority priority, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = ReadLastId(store) + 1;
            var record = new CardRecord(
                id, title.Trim(), string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
                priority, columnId, (body ?? string.Empty).Trim(), Array.Empty<TaskState>());
            // Advance the counter and write the card in one transaction so a card's id is never
            // reused even if it (or a later card) is subsequently deleted.
            await store.Save(new[]
            {
                new Kvp(CounterKey, new CardCounter(id)),
                new Kvp(CardKey(id), record),
            }).ConfigureAwait(false);
            return id;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Move a card to another column by rewriting its <see cref="CardRecord.ColumnId"/>.
    /// Returns false when no card with that id exists.</summary>
    public Task<bool> MoveCardAsync(int cardId, string toColumnId, CancellationToken ct)
        => MutateCardAsync(cardId, card => card with { ColumnId = toColumnId }, ct);

    /// <summary>Delete a card. The card-id counter is left untouched, so the deleted id is never
    /// handed out again.</summary>
    public async Task<bool> DeleteCardAsync(int cardId, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<CardRecord>(CardKey(cardId)) is null) return false;
            await store.Save(new Kvp(CardKey(cardId), null)).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Apply a parsed card blob (from <c>board_update_card</c>): set the body and overwrite
    /// title/branch/priority where the frontmatter supplied them. The card's tasks are a separate
    /// collection and are left untouched. Returns false when no card with that id exists.</summary>
    public Task<bool> UpdateCardContentAsync(int cardId, CardMarkdown.ParsedCard parsed, CancellationToken ct)
        => MutateCardAsync(cardId, card => card with
        {
            Title = parsed.Title ?? card.Title,
            Branch = parsed.Branch ?? card.Branch,
            Priority = parsed.Priority ?? card.Priority,
            Body = parsed.Body,
        }, ct);

    /// <summary>Update a card's title and body (the prose description). Used by the card detail dialog.
    /// The card's tasks are managed separately and left untouched. Returns false when no card with that
    /// id exists.</summary>
    public Task<bool> UpdateCardBodyAsync(int cardId, string title, string body, CancellationToken ct)
        => MutateCardAsync(cardId, card => card with
        {
            Title = title.Trim(),
            Body = (body ?? string.Empty).Trim(),
        }, ct);

    /// <summary>Append one or more tasks to a card, assigning each the next card-local id from the
    /// card's <see cref="CardRecord.LastTaskId"/> counter (advanced past any ids already present, so
    /// ids are never reused even after deletes). Blank texts are skipped. Returns the card's full task
    /// list after the add, or null when no card with that id exists.</summary>
    public async Task<IReadOnlyList<TaskItem>?> AddTasksAsync(int cardId, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<CardRecord>(CardKey(cardId)) is not { } card) return null;
            card = NormalizeTaskIds(card);

            var tasks = card.Tasks.ToList();
            var nextId = Math.Max(card.LastTaskId, tasks.Count == 0 ? 0 : tasks.Max(t => t.Id));
            foreach (var text in texts)
            {
                var trimmed = (text ?? string.Empty).Trim();
                if (trimmed.Length == 0) continue;
                tasks.Add(new TaskState(++nextId, trimmed, false));
            }

            var updated = card with { Tasks = tasks, LastTaskId = nextId };
            await store.Save(new Kvp(CardKey(cardId), updated)).ConfigureAwait(false);
            return ToTasks(updated);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Set a single task's done flag. Returns false when no card with that id exists or the
    /// card has no task with that task id.</summary>
    public Task<bool> SetTaskDoneAsync(int cardId, int taskId, bool done, CancellationToken ct)
        => MutateTaskAsync(cardId, taskId, t => t with { Done = done }, ct);

    /// <summary>Replace a single task's text (blank is rejected). Returns false when no card with that
    /// id exists or the card has no task with that task id.</summary>
    public Task<bool> UpdateTaskTextAsync(int cardId, int taskId, string text, CancellationToken ct)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return Task.FromResult(false);
        return MutateTaskAsync(cardId, taskId, t => t with { Text = trimmed }, ct);
    }

    /// <summary>Delete a single task from a card. The card's <see cref="CardRecord.LastTaskId"/> counter
    /// is left untouched so the deleted task id is never handed out again. Returns false when no card
    /// with that id exists or the card has no task with that task id.</summary>
    public async Task<bool> RemoveTaskAsync(int cardId, int taskId, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<CardRecord>(CardKey(cardId)) is not { } card) return false;
            card = NormalizeTaskIds(card);
            var tasks = card.Tasks.ToList();
            if (tasks.RemoveAll(t => t.Id == taskId) == 0) return false;
            await store.Save(new Kvp(CardKey(cardId), card with { Tasks = tasks })).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Set a card's priority. Returns false when no card with that id exists.</summary>
    public Task<bool> SetPriorityAsync(int cardId, CardPriority priority, CancellationToken ct)
        => MutateCardAsync(cardId, card => card with { Priority = priority }, ct);

    /// <summary>Bind a card to a git branch. Returns false when no card with that id exists.</summary>
    public Task<bool> SetBranchAsync(int cardId, string branch, CancellationToken ct)
        => MutateCardAsync(cardId, card => card with { Branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim() }, ct);

    /// <summary>Create or replace a card's named artifact (its durable step output — a plan, notes, a
    /// log), keyed by <paramref name="kind"/>. A re-set of the same kind overwrites it and restamps its
    /// time; blank <paramref name="content"/> removes the artifact (a no-op when none exists). Other
    /// card fields, tasks, and artifacts are left untouched. Returns false when no card with that id
    /// exists.</summary>
    public Task<bool> SetArtifactAsync(int cardId, string kind, string? content, CancellationToken ct)
    {
        var trimmedKind = (kind ?? string.Empty).Trim();
        return MutateCardAsync(cardId, card =>
        {
            var list = (card.Artifacts ?? Array.Empty<ArtifactState>()).ToList();
            var idx = list.FindIndex(a => string.Equals(a.Kind, trimmedKind, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(content))
            {
                // Blank content is the delete gesture; replacing in place would otherwise store an empty artifact.
                if (idx >= 0) list.RemoveAt(idx);
            }
            else
            {
                var updated = new ArtifactState(trimmedKind, content, DateTimeOffset.UtcNow);
                if (idx >= 0) list[idx] = updated; else list.Add(updated);
            }
            return card with { Artifacts = list };
        }, ct);
    }

    /// <summary>Set (or clear, when blank) a column's instructions. Returns false when no column with
    /// that id exists.</summary>
    public async Task<bool> SetColumnInstructionsAsync(string columnId, string? instructions, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<ColumnRecord>(ColumnKey(columnId)) is not { } col) return false;
            var normalized = string.IsNullOrWhiteSpace(instructions) ? null : instructions;
            await store.Save(new Kvp(ColumnKey(columnId), col with { Instructions = normalized })).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restore the board to the default column layout. Existing columns are matched to defaults
    /// by display name (order + instructions restored); missing defaults are created. Cards already in a
    /// default column stay; cards elsewhere move to the idea column (or are deleted when
    /// <paramref name="wipe"/>). Non-default columns are then removed.</summary>
    public async Task<ReinitResult> ReinitAsync(bool wipe, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var columns = ReadColumns(store).ToList();
            var byName = columns
                .GroupBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var saves = new List<Kvp>();
            int columnsCreated = 0, instructionsRestored = 0;
            var defaultColumnIds = new HashSet<string>(StringComparer.Ordinal);
            string ideaColumnId = string.Empty;

            foreach (var (order, displayName, instructions) in BoardService.DefaultColumns)
            {
                if (byName.TryGetValue(displayName, out var existing))
                {
                    if (existing.Order != order || !string.Equals(existing.Instructions, instructions, StringComparison.Ordinal))
                    {
                        saves.Add(new Kvp(ColumnKey(existing.Id), existing with { Order = order, Instructions = instructions }));
                        instructionsRestored++;
                    }
                    defaultColumnIds.Add(existing.Id);
                    if (order == BoardService.DefaultColumns[0].Order) ideaColumnId = existing.Id;
                }
                else
                {
                    var id = NewColumnId();
                    saves.Add(new Kvp(ColumnKey(id), new ColumnRecord(id, order, displayName, instructions)));
                    defaultColumnIds.Add(id);
                    columnsCreated++;
                    if (order == BoardService.DefaultColumns[0].Order) ideaColumnId = id;
                }
            }

            int cardsPreserved = 0, cardsMoved = 0, cardsWiped = 0;
            foreach (var card in ReadCards(store))
            {
                if (wipe)
                {
                    saves.Add(new Kvp(CardKey(card.Id), null));
                    cardsWiped++;
                }
                else if (defaultColumnIds.Contains(card.ColumnId))
                {
                    cardsPreserved++;
                }
                else
                {
                    saves.Add(new Kvp(CardKey(card.Id), card with { ColumnId = ideaColumnId }));
                    cardsMoved++;
                }
            }

            // A wipe is the explicit "start the whole board over" gesture, so reset the id counter:
            // the next card created numbers from 1 again. A non-wipe reinit keeps surviving cards and
            // their ids, so its counter is left advanced — resetting it would collide with them.
            if (wipe) saves.Add(new Kvp(CounterKey, null));

            // Remove every column that isn't one of the defaults (their cards have been relocated above).
            foreach (var col in columns.Where(c => !defaultColumnIds.Contains(c.Id)))
                saves.Add(new Kvp(ColumnKey(col.Id), null));

            if (saves.Count > 0) await store.Save(saves.ToArray()).ConfigureAwait(false);

            return new ReinitResult(columnsCreated, instructionsRestored, cardsPreserved, cardsMoved, cardsWiped);
        }
        finally { _gate.Release(); }
    }

    // ---- internals -----------------------------------------------------------------------------

    private async Task<bool> MutateCardAsync(int cardId, Func<CardRecord, CardRecord> mutate, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<CardRecord>(CardKey(cardId)) is not { } card) return false;
            await store.Save(new Kvp(CardKey(cardId), mutate(card))).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    // Replace a single task (matched by its card-local id) in place via <paramref name="mutate"/>.
    // Returns false when the card or the task id is missing, so a no-match is a clean failure rather
    // than a silent rewrite.
    private async Task<bool> MutateTaskAsync(int cardId, int taskId, Func<TaskState, TaskState> mutate, CancellationToken ct)
    {
        var store = await _yamca.GetAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (store.Get<CardRecord>(CardKey(cardId)) is not { } card) return false;
            card = NormalizeTaskIds(card);
            var tasks = card.Tasks.ToList();
            var idx = tasks.FindIndex(t => t.Id == taskId);
            if (idx < 0) return false;
            tasks[idx] = mutate(tasks[idx]);
            await store.Save(new Kvp(CardKey(cardId), card with { Tasks = tasks })).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    private static Task SeedDefaultColumnsAsync(VestPocketStore store, CancellationToken ct)
    {
        var seeds = new List<Kvp>();
        foreach (var (order, displayName, instructions) in BoardService.DefaultColumns)
        {
            var id = NewColumnId();
            seeds.Add(new Kvp(ColumnKey(id), new ColumnRecord(id, order, displayName, instructions)));
        }
        return store.Save(seeds.ToArray()).AsTask();
    }

    private static string NewColumnId() => Guid.NewGuid().ToString("n");

    /// <summary>The most recently assigned card id (0 when no card has ever been created). Read from
    /// the persisted <see cref="CardCounter"/>; the next id is always this + 1.</summary>
    private static int ReadLastId(VestPocketStore store)
        => store.Get<CardCounter>(CounterKey)?.LastId ?? 0;

    private static List<ColumnRecord> ReadColumns(VestPocketStore store)
    {
        var list = new List<ColumnRecord>();
        foreach (var kv in store.GetByPrefix(ColumnPrefix))
            if (kv.Value is ColumnRecord c) list.Add(c);
        return list;
    }

    private static List<CardRecord> ReadCards(VestPocketStore store)
    {
        var list = new List<CardRecord>();
        foreach (var kv in store.GetByPrefix(CardPrefix))
            if (kv.Value is CardRecord c) list.Add(c);
        return list;
    }

    private static BoardCard ToCard(CardRecord r) => new(
        r.Id, r.Title, r.Branch, r.ColumnId, r.Body, ToTasks(r), r.Priority)
    {
        // Coalesce: a card persisted before the Artifacts field existed deserializes with a null list.
        Artifacts = (r.Artifacts ?? Array.Empty<ArtifactState>())
            .Select(a => new CardArtifact(a.Kind, a.Content, a.UpdatedAt)).ToList(),
    };

    // Project a record's tasks to the read model, renumbering legacy id-less lists (see RenumberLegacy).
    private static IReadOnlyList<TaskItem> ToTasks(CardRecord r)
        => RenumberLegacy(r.Tasks ?? Array.Empty<TaskState>())
            .Select(t => new TaskItem(t.Id, t.Text, t.Done)).ToList();

    // Persist-side counterpart to the read projection: when a card's tasks are legacy id-less (id 0),
    // bake in the same positional ids the read model exposes — and advance LastTaskId past them — before
    // a by-id mutation, so persisted ids match what the caller saw. A no-op once tasks carry ids.
    private static CardRecord NormalizeTaskIds(CardRecord card)
    {
        var tasks = card.Tasks ?? Array.Empty<TaskState>();
        if (tasks.All(t => t.Id != 0)) return card with { Tasks = tasks.ToList() };
        var renumbered = RenumberLegacy(tasks);
        return card with { Tasks = renumbered, LastTaskId = Math.Max(card.LastTaskId, renumbered.Count) };
    }

    // Legacy data (persisted before tasks carried ids, or under the old body-checklist "Subtasks" shape)
    // deserializes with every id 0; renumber such a list positionally 1..n so ids are stable and distinct.
    // A list whose tasks already carry ids is returned as-is.
    private static List<TaskState> RenumberLegacy(IReadOnlyList<TaskState> tasks)
        => tasks.Any(t => t.Id == 0)
            ? tasks.Select((t, i) => new TaskState(i + 1, t.Text, t.Done)).ToList()
            : tasks.ToList();
}
