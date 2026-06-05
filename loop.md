# Loop / batch fan-out — design notes

Status: **v1 built (LLM-facing `loop` tool + engine).** The engine
(`Yamca.Agent/Subagents/BatchRunner.cs`), the `loop` tool
(`Yamca.Agent/Tools/LoopTool.cs`), and the shared structured-outcome foundation
(`SubagentOutcome`, `subagent_result`'s baked status) are implemented. The
**user/board-facing batch action is deferred** — its trigger model (column-driven
vs filter-driven vs other) is still undecided. See also `doc/dev-board.md` (the
board-driven framing) and `Yamca.Agent/Subagents/SubagentRunner.cs` (the
primitive this builds on). Decisions captured here so we don't re-litigate them.

---

## What we're building

A way to run one prompt over many items, each item handled by an isolated
subagent session, then reduce the per-item outcomes to a single aggregate the
caller sees. Conceptually **map-reduce over `subagent_run`**.

Example: the parent passes a list of markdown filenames, the `analyze` subagent,
and a prompt ("analyze this file and create a dev-board card if <condition>").
The loop runs 20 isolated sessions and returns one roll-up: "20 analyzed, 17
succeeded, 3 need follow-up, 0 failed."

### Why it's worth a first-class feature (not just repeated `subagent_run`)

The parent *can* already call `subagent_run` N times itself. The loop earns its
place by doing three things that the manual approach can't:

1. **Context collapse** — the headline benefit. N subagent runs would otherwise
   leave N tool calls + N results in the parent transcript, consuming context
   the subagent layer exists to protect. The loop collapses them into one call
   returning one roll-up.
2. **Bounded concurrency** — the loop can run items in parallel (with a cap); a
   hand-written sequence of `subagent_run` calls is serial.
3. **Structured aggregation** — a mechanical roll-up with failures surfaced,
   instead of the parent eyeballing N prose blobs.

---

## Two faces, one engine

Factor the fan-out/reduce logic into a shared engine (working name
`BatchRunner`) that sits beside `SubagentRunner`, and expose it two ways:

- **LLM-facing `loop` tool** — autonomous, mid-task fan-out the model decides on.
- **User/board-facing batch action** — deterministic, user-initiated batch jobs
  ("advance every card in column `20-analyze`"). This is the more yamca-native
  framing: the dev board already models columns + cards + per-column
  `instructions.md`, and a card tracks its own state, so a board-driven batch
  gets **resumability and re-run-the-failures for free** — properties a pure
  tool call (which loses everything if it dies at item 30 of 50) can't offer.

Build the engine once; both faces call it. Don't ship only the tool.

---

## Outcome reporting — the core decision

### The gap today

A subagent has **no explicit way to report failure**. `SubagentResultTool` takes
only `result: string`, always returns `ToolResult.Ok`, and `SubagentRunner`
returns `ToolResult.Ok(result)` the moment it's called. The only *structured*
failures are **mechanical**: the run hit its iteration cap, stopped without
calling `subagent_result`, or was cancelled — the runner infers those from how
the loop ended (`TurnCompletionReason`), not from anything the subagent said.

So there are two paths today, and they don't line up with "did the task succeed":

| What happened | What the caller sees today |
|---|---|
| Subagent delivered a normal answer | `ToolResult.Ok` + prose |
| Subagent delivered "I couldn't do it because X" | **`ToolResult.Ok` + prose** ← looks like success |
| Subagent stalled / cap / cancelled | `ToolResult.Error` + reason |

The middle row is the trap: **semantic** failure (the subagent concluded "no")
is invisible without reading the prose — i.e. an outer model call, which is
exactly what we're trying to avoid.

### Decision: bake a fixed three-value status

Give loop-mode subagents a result tool whose status is a baked enum:

```
loop_item_result({
  status: "success" | "failure" | "needs_followup",
  summary: string,        // one line, the subagent's own words
})
```

- The **inner** subagent fills `status` — it's reasoning anyway, so this costs
  **nothing in the outer context**.
- The **reducer** counts statuses mechanically. It never parses prose and never
  calls a model.
- **Mechanical** failures (cap / no-delivery / cancel) remain a separate,
  runner-derived outcome and map to a failed item regardless of status. Semantic
  and mechanical failure are different things; both end up structured.

### Decision: NO caller-defined label/status vocabulary

We considered letting the caller pass an open vocabulary (either open `status`
values, or a `labels: string[]` axis layered on top) so the reducer could count
domain-specific outcomes like "needs-separate-analysis". **Rejected.**

- Open `status` values force the caller to *also* declare which values mean
  failure, or the reducer can't produce "X ok, Y failed" — re-introducing the
  ambiguity the whole exercise removes.
- A separate `labels` axis works but adds a mapped vocabulary, schema injection,
  and prompt machinery for marginal gain.

The three statuses are enough. **The calling LLM already knows what
`success` / `failure` / `needs_followup` mean for *this* batch, because it wrote
the prompt that defines the task.** `needs_followup` is the catch-all for "ran
fine, but this one needs another look" (the "3 require separate analysis" case).
Keep it simple; let the prompt carry the domain meaning.

### Reducer output

Mechanical roll-up, no model call. Counts up top, failures verbatim (we already
build good failure tails in `SubagentRunner.FailureMessage`), successes compact:

```
Loop over 20 items with agent 'analyze': 17 success, 3 needs_followup, 0 failed.
  needs_followup:
    docs/auth.md    — <subagent summary>
    docs/legacy.md  — <subagent summary>
    docs/old-api.md — <subagent summary>
  [success items: compact one-liners]
```

Narrative synthesis over the `summary` blobs *does* cost an outer model call;
keep it opt-in and visible, never the silent default.

---

## Guardrails (a session-per-item amplifies cost fast)

- **Hard item cap**, with a clear error when exceeded.
- **Bounded concurrency, opt-in.** Default **serial for write-heavy loops** —
  the dev-board-creation example does concurrent writes against `.yamca/board/`
  (ID allocation collisions). Parallelism is safe for read-heavy analyze loops.
- **Cancellation** threaded through (per-run `CancellationToken` already exists).
- **Grouped observability.** A loop spawns N runs; the existing
  `ISubagentObserver` → `SubagentLiveSession` → `SubagentSessionRegistry` UI
  pipeline should group them under the loop call (a `LoopRunInfo` parent id).
- **No nested loops.** `subagent_run` is already excluded from child tool sets so
  subagents can't recurse; exclude `loop` from child registries too.

---

## Scope boundary — when NOT to use it

A full headless `AgentLoop` per item is heavy. The loop is for items where **each
one is worth its own isolated reasoning session** (the same bar `subagent_run`
sets: "well-scoped, context-heavy subtasks"). For light per-item transforms
("extract one field from 100 rows"), the parent should work inline or reach for a
script — looping in-process is far cheaper than N model sessions. State this in
the tool description so `loop` doesn't become the hammer for every iteration.

---

## Resolved (v1)

- **Item shape** — array of strings; the parent enumerates first via
  `find_files`/`grep` and passes an explicit list. Richer item objects and
  tool-side selectors/globs remain deferred.
- **Prompt + item joining** — **append** (`<prompt>\n\nItem: <item>`). Implemented
  in `BatchRunner.RunAsync`.
- **Explicit status applies to ALL subagent runs** — `subagent_result` now takes a
  required `status` (`success`/`failure`/`needs_followup`). `SubagentRunner` maps
  it to a `ToolResult` for plain `subagent_run`: semantic `failure` → error,
  `needs_followup` → `Ok` tagged `[needs follow-up]`, `success` → `Ok`. The batch
  engine consumes the structured `SubagentOutcome` directly for the roll-up.
- **Concurrency** — `BatchRunner` runs strictly **serial** in v1. It carries a
  `_maxConcurrency` field (=1) documenting the future axis, but no tool param or
  setting exposes parallelism yet — that's intended to be gated behind coordinated
  user settings, not a bare tool param.
- **Hard item cap** — `BatchRunner.MaxItems = 50`, rejected with a clear error.

## Still open

- **User/board-facing batch action** — not built. Decide its trigger model:
  column-driven ("advance every card in `20-analyze`"), filter-driven, or other.
  When built, it calls the same `IBatchRunner` engine.
- **Narrative synthesis** over the per-item summaries (an opt-in outer model call)
  — deferred; the reducer stays purely mechanical for now.
- **Grouped-loop UI** — the data is groupable (`SubagentRunInfo.LoopRunId`,
  `SubagentSessionRegistry.ByLoopId`), but no loop-parent card is rendered yet;
  each child run still shows individually in the live view.
