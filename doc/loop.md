# Loop

The `loop` tool runs **one prompt over many items**, each handled by its own
isolated [subagent](subagents.md) session, and returns a single roll-up of the
outcomes. It is the fan-out/reduce counterpart to `subagent_run`: where
`subagent_run` delegates one task, `loop` delegates the *same* task across a list
and collapses N independent results into one compact summary the parent can act
on.

## When to use it

Reach for `loop` only when **each item genuinely deserves its own reasoning
session** — e.g. "review each of these 12 changed files", "for each failing test,
find the likely cause", "summarize each of these documents". If the work is a
simple mechanical pass, a single subagent (or the parent itself) is cheaper; a
session-per-item amplifies cost fast.

The payoff over hand-writing N `subagent_run` calls is threefold:

- **Context collapse** — the parent gets one roll-up (counts plus the failures
  verbatim) instead of N transcripts cluttering its window.
- **A hard item cap** — at most **50** items per call, so a runaway enumeration
  can't spawn unbounded sessions.
- **Mechanical aggregation** — successes and failures are tallied by the engine
  from each item's declared status, with no outer model needed to read prose.

## How a call works

The parent calls `loop` with three arguments:

- **agent** — the configured subagent to run for every item (same catalog as
  `subagent_run`).
- **prompt** — the task applied to each item. It must be **self-contained** and
  must define success vs. failure explicitly, so every item reports its status
  the same way. Put the placeholder `{{item}}` (single-brace `{item}` also works)
  where the item belongs and it is substituted in; omit it and the item is
  appended as a trailing `Item: <item>` line instead.
- **items** — the list to run over (e.g. filenames the parent enumerated first).
  One isolated subagent session runs per item.

Each item runs through the **same machinery as a single `subagent_run`** (agent
lookup, endpoint resolution, curated tools, the `subagent_result` protocol, live
observability). Runs execute **serially** in the current version — write-heavy
loops would otherwise race on shared state — and a cancellation stops the loop
after the in-flight item.

## The roll-up

When the loop finishes, the parent receives a single summary: a header line with
the success / needs_followup / failed counts, then the per-item results grouped
by status. **Failures and follow-ups are shown verbatim** (they are the
actionable part); successes are listed compactly. For example:

```
Loop over 5 items with agent 'explorer': 3 success, 1 needs_followup, 1 failed.
  failed:
    src/legacy/Parser.cs   — Could not locate the symbol; the file appears to be generated.
  needs_followup:
    src/Api/Handler.cs     — Found two candidates; a human should confirm which is intended.
  success:
    src/Core/Engine.cs     — …
```

## Watching a loop

Like single delegations, every per-item run streams into the **Subagent
sessions** viewer (the chat toolbar button), grouped under the parent loop so you
can watch each item work and inspect any that failed. See
[subagents.md](subagents.md#watching-a-run).

## Limits

- **At most 50 items** per call; narrow the list or split it across calls.
- **No nesting.** `loop` is excluded from every subagent's tool set, so a loop's
  subagents cannot start loops of their own.
- **Hidden when unconfigured.** With no subagents defined there is nothing to loop
  over, so the tool removes itself from the parent's tool set entirely.

## See also

- [subagents.md](subagents.md) — the headless agents a loop fans work out to
- [tools-and-permissions.md](tools-and-permissions.md) — the tools each item's subagent may use
- [chat-sessions.md](chat-sessions.md) — the parent conversation that launches the loop
