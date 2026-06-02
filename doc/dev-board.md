# Dev Board

The dev board is a Kanban-style board for driving development work through a
pipeline of AI-assisted steps. It lives at `/board` in the web UI.

## Where the board lives

The board is stored on a dedicated `yamca-board` git branch, checked out as a
worktree at `.yamca/board`. It is tracked independently of any code branch, so
board edits never touch your working code. Each board mutation (add, move,
edit, delete) is committed to the `yamca-board` branch automatically.

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

- **Create** — the `+` button on the first column adds a card.
- **Priority** — `high` / `normal` / `low`; cards sort high → normal → low
  within a column. High/low priority is shown on the card.
- **Subtasks** — GitHub-style `- [ ]` / `- [x]` checklist lines in the body
  render as a done/total count on the card.
- **Branch binding** — when a step runs, the card is bound to a git branch via
  `branch:` frontmatter, so all steps for that card share one branch.
- **Edit** — open a card to edit its title and description inline.

## Moving cards

- **Drag and drop** a card between lanes. Moves are optimistic (the card jumps
  immediately) with the git commit running in the background; a spinner overlay
  shows the in-flight work.
- **Promote** a card to the next column from the card detail dialog.

## Running a step

Opening a card in a work column (or using the per-card play button) starts an
AI chat session:

1. The card is bound to its branch (`branch:` frontmatter committed).
2. A code worktree is forked off the base branch (or reused/recreated if it was
   deleted or merged).
3. The column's instructions plus the next-column context seed the session.
4. You're navigated to the chat with the composer pre-filled (not yet sent).

The play button appears on cards in work columns when at least one endpoint is
configured. Up to 4 concurrent chat sessions are supported.

## Branch-bound actions

When a card's branch has a live worktree, the card detail dialog offers:

- **Open chat** — start an interactive session on the card's branch worktree
  for follow-up work or conflict resolution.
- **Merge** — merge the card's branch back into the base branch (reusing the
  standard branch-merge dialog), with optional worktree cleanup.

If the branch was merged or deleted, the card offers to run fresh instead.

## Filtering and restore

- **Filter** (search icon) — filter cards by title, or by full card text, with
  starts-with or contains matching. An active filter shows as a chip.
- **Restore deleted card** (trash icon) — recover a previously deleted card
  from the board branch's git history into a chosen column.

## Changes tab

A card bound to a live worktree exposes a **Changes** tab in its detail dialog
that diffs the worktree against its branch's fork point.
