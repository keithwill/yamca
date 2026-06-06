# Dev Board

The dev board is a Kanban-style board for driving development work through a
pipeline of AI-assisted steps. It lives at `/board` in the web UI.

## Where the board lives

The board is a personal scratchpad — a plain, **uncommitted** directory at
`.yamca/board` (gitignored), for the current user's immediate work. It is local
only: never committed, tracked, or pushed, so board churn is never shared with
your team. Each board mutation (add, move, edit, delete) is just a file write.
Because it sits at the repository root, the one board is shared across every
chat session regardless of which code branch or worktree the session is on.

## Columns

Columns are materialized from numeric-prefixed directories (e.g.
`10-idea`, `30-implement`). The numeric prefix sets the column order; the
remainder is the display name. The default layout is
**idea → analyze → implement → verify → done**.

- A column whose `instructions.md` has content is a **work step**: opening or
  running a card there starts an AI chat session seeded with those
  instructions. Work columns show a chat icon in the header.
- A column with an empty `instructions.md` is a **resting column**: cards are
  simply promoted onward without running a step.

Edit a column's instructions via the gear icon on its header.

## Cards

A card is a single markdown file living in its current column's directory, with
YAML frontmatter (`id`, `title`, optional `priority` and `branch`) and a
markdown body.

- **Create** — the `+` button on the first column adds a card. The new-card
  dialog includes a branch field that defaults to the id-prefixed slug of the
  title (tracking the title as you type, until you edit the branch yourself).
  Every card is therefore born with a `branch:` already decided.
- **Priority** — `high` / `normal` / `low`; cards sort high → normal → low
  within a column. High/low priority is shown on the card.
- **Subtasks** — GitHub-style `- [ ]` / `- [x]` checklist lines in the body
  render as a done/total count on the card.
- **Branch** — the `branch:` frontmatter names the git branch the card lives on,
  shared by all its steps. It is editable from the card detail dialog on any
  card not yet bound to a live worktree — including cards in resting columns —
  and a change is saved on close. Choosing a branch does **not** create a
  worktree; that waits for the first step run or chat opened on the card.
- **Edit** — open a card to edit its title and description inline.

## Moving cards

- **Drag and drop** a card between lanes. Moves are optimistic (the card jumps
  immediately) with the file move running in the background; a spinner overlay
  shows the in-flight work.
- **Promote** a card to the next column from the card detail dialog.

## Running a step

Opening a card in a work column (or using the per-card play button) starts an
AI chat session:

1. The card is bound to its branch (`branch:` frontmatter written).
2. A code worktree is forked off the base branch (or reused/recreated if it was
   deleted or merged).
3. The card (title + body) plus the column's `instructions.md` seed the session.
4. You're navigated to the chat with the seeded prompt pre-filled and sent automatically.

The play button appears on cards in work columns when at least one endpoint is
configured. Up to 4 concurrent chat sessions are supported.

## Branch actions

- **Open chat** — start an interactive session on the card's branch for
  follow-up work or conflict resolution. Offered for any card with a branch
  defined (and a free chat slot); the branch and its worktree are created on
  demand when none exists yet.

When a card's branch has a live worktree, the card detail dialog additionally
offers:

- **Merge** — merge the card's branch back into the base branch (reusing the
  standard branch-merge dialog), with optional worktree cleanup.

If the branch was merged or deleted, the card offers to run fresh instead.

## Filtering

- **Filter** (search icon) — filter cards by title, or by full card text, with
  starts-with or contains matching. An active filter shows as a chip.

Because the board is uncommitted, deleting a card is permanent — there is no
git history to recover it from.

## Changes tab

A card bound to a live worktree exposes a **Changes** tab in its detail dialog
that diffs the worktree against its branch's fork point.
