# Orchestrator

The orchestrator turns the [dev board](dev-board.md) into an autonomous pipeline: when
enabled, it picks up cards in the work columns you choose and runs each one through a
headless agent session — no browser interaction needed. It is inspired by the semantics of
[OpenAI's Symphony spec](https://github.com/openai/symphony), adapted to yamca's local
board instead of an external issue tracker.

## How a run works

Every poll interval the orchestrator reads the board and, for each enabled column, picks
eligible cards in display order (priority high → normal → low, then oldest id). For each
dispatched card it:

1. Binds the card to its branch (the same branch write as Run Step) and
   provisions the branch worktree, reusing a live one when it exists.
2. Seeds a headless agent session with the card (title + body) plus the column's
   instructions — exactly the prompt an interactive Run Step would send.
3. Lets the agent work with the configured tool set, **auto-approving every tool call**.
4. Considers the run complete when the card *leaves its source column* — normally because
   the agent called `board_move_card`, per the column instructions. If the agent stops
   without moving the card, the orchestrator nudges it with a continuation prompt, up to
   the max-turns cap.
5. Saves the full transcript to chat history (`Orchestrator: <card> — <column>`), so every
   run — including failed ones — can be reviewed later.

Because success is "the card left the column", moving a card yourself while it is being
worked simply cancels that run. Deleting the card does too.

If a card's next column is also enabled, the next poll picks it up there — cards **chain
through the pipeline** automatically until they reach a resting or disabled column. Enable
only `plan` for a human checkpoint after planning, or the whole pipeline for end-to-end
runs that stop at `done`.

## Enabling and disabling

The switch lives on the board toolbar and on the orchestrator settings page. The on/off
state is **never persisted**: yamca always starts with orchestration off, so a restart can
never silently start burning tokens. Disabling cancels every in-flight run immediately.

While enabled, cards show a status badge: *queued*, *running* (click to watch the live
transcript), *retrying* (with the failure and next attempt time), or *parked*.

## Failures, retries, and parking

A run that fails — endpoint error, stall (no model output for the stall timeout), turn
timeout, or simply never moving the card within its turn budget — is retried with
exponential backoff (first-delay × 2 per attempt, capped). After the configured attempts
the card is **parked**: skipped until you move or edit it, click retry on its badge, or
toggle the orchestrator off and on (re-enabling clears all parked state).

## Configuration

Settings live in the **project tier** (`.yamca/project.json`) — which columns are
automatable is a property of this repository's workflow. The orchestrator re-reads them
every poll, so changes apply to future dispatch without a restart; invalid configuration
(missing endpoint, a disabled column, an empty tool list) pauses dispatch and shows the
error, while running cards are still reconciled.

| Setting | Notes |
|---|---|
| **Enabled columns** | Work columns whose cards are dispatched autonomously. |
| **Endpoint** | Which configured endpoint runs use; default endpoint when unset. |
| **Max concurrent runs / per-column cap** | Global and optional per-column concurrency. Start small (the default is 2) — local models rarely benefit from more. |
| **Max turns per run** | Seed turn plus continuation nudges before the run counts as failed. |
| **Tool iterations per turn** | Tool-call round-trips per turn; inherits the session default when unset. |
| **Stall / turn timeout** | A turn with no model output for the stall timeout, or running past the turn timeout, is cancelled and retried. |
| **Max attempts / retry delays** | The backoff-and-park failure policy above. |
| **Allowed tools** | The agent's tool set for orchestrated runs (see below). |
| **Restrict to workspace** | Confines supporting tools to the run's worktree. |

## Safety

Orchestrated runs are autonomous: **every tool in the allowed list is auto-approved**,
with no Ask prompts. With the default tool set that includes `git` — column instructions
tell the agent to commit, so expect autonomous commits on each card's branch. What still
bounds a run:

- the allowed-tools list (`subagent_run` and `loop` are always excluded, so a run can
  never fan out further agents);
- workspace restriction to the card's worktree;
- the turn / iteration caps and the stall / turn timeouts;
- the concurrency limits;
- merging stays manual — agents work on card branches, and nothing lands on your base
  branch until you merge it from the card dialog.

Start with the curated default tool set and low concurrency, and add execute tools only
once you trust the column instructions you've written.

One caution: don't run a step interactively on a card the orchestrator currently owns —
both sessions would share the same branch worktree.

## See also

- [dev-board.md](dev-board.md) — columns, cards, instructions, and the interactive Run Step.
- [worktrees.md](worktrees.md) — how branch worktrees isolate each card's work.
- [chat-sessions.md](chat-sessions.md) — where persisted run transcripts appear.
