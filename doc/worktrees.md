# Worktrees

yamca runs much of its work on **git worktrees** — separate checkouts of a branch
living under `.yamca/worktrees/<branch>`. This lets a chat session (or a board
step) make changes on an isolated branch without disturbing your main checkout.
The `/workspace` page shows the current workspace and the repository's worktrees.

## RootPath vs. RepositoryRoot

A workspace exposes two distinct paths (`IWorkspace`):

- **RootPath** — the sandbox boundary: the directory the session was opened to.
  All agent file operations are clamped to this path. It also doubles as the key
  that ties a worktree session back to its base chat session.
- **RepositoryRoot** — the top level of the containing git repository (or
  `RootPath` itself when not in a repo). Repo-scoped artifacts anchor here so
  they resolve to the same place regardless of which subdirectory a session
  opened to.

Repo-scoped artifacts that live at `RepositoryRoot`:

- `.yamca/board` — the dev board worktree (see [dev-board.md](dev-board.md))
- `.yamca/worktrees` — code-branch worktrees
- `.yamca/chat` — saved chat sessions (see [chat-sessions.md](chat-sessions.md))

`RepositoryRoot` is **not** a sandbox boundary and may sit above `RootPath`.

## How worktrees are created

When a branch needs a worktree (e.g. a board step binds a card to a branch),
yamca resolves it in this order:

1. A live worktree already checked out on the branch → reuse it.
2. The branch exists but has no worktree → add one at
   `.yamca/worktrees/<sanitized-branch>`.
3. Neither exists (new card branch, or one that was merged/deleted) → create the
   branch *and* worktree, forked off the base branch.

Branch names are sanitized for the directory (slashes become dashes).

## Merging

A branch's work is merged back into the base branch through the branch-merge
dialog (shared by the chat panel and the board's card dialog). The merge runs
against the repository's base checkout — anchored at `RepositoryRoot`, not the
sandbox `RootPath` — with optional post-merge worktree cleanup. If the worktree
was already removed, cleanup falls back to the conventional path so removal is a
harmless no-op rather than a crash.

## Relationship to the board

The dev board is itself a worktree (the `yamca-board` branch), tracked
independently of code. Board *cards* bind to *code* worktrees when their steps
run. The board enumerates code-branch worktrees only to decide a card's
liveness affordances — a card whose branch has a live worktree offers
merge / open-chat; one whose branch never started or was merged away offers
run-fresh instead.

## See also

- [dev-board.md](dev-board.md) — cards bind to branches and fork worktrees per step
- [chat-sessions.md](chat-sessions.md) — sessions run against worktrees
