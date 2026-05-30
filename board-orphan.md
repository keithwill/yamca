# Board on an orphan branch — execution plan

> Status: ready-to-execute plan. Moves board state off per-card code branches onto a single
> dedicated **orphan branch** (`yamca-board`), checked out by yamca into a fixed worktree.
> Backwards compatibility is **out of scope** (no users yet) — we migrate yamca's own
> dogfooded board as part of the work and do not support the old layout.

---

## 1. Motivation (why change where cards live)

Today the board is git-native in the strong sense: card files live at `.yamca/board` on
**whatever branch is checked out**, and a card bound to a branch moves through columns
**inside that branch's worktree**. A move is `git mv`-staged but not committed
(`BoardMoveCardTool`) so the status change can ride in the same commit as the code.

That coupling concentrates its cost in one method — `BoardService.ReadForDisplay` — and a
web of supporting behavior:

- **`ReadForDisplay` overlay** — per-worktree snapshot cache, re-bucketing by column,
  "keep the root path but overlay the worktree's column," and fallbacks for *branch never
  started / merged / deleted / card missing from worktree*.
- **Frozen-card problem** — a bound card looks stuck in its origin column on the root board
  until merged; the overlay exists only to compensate.
- **Split-brain canonicality** — which branch's copy of card #7 is true?
- **Board-vs-code merge conflicts** — board churn rides inside code merges.
- **Two behaviors for one move primitive** — `BoardMoveCardTool` stages for bundling; the
  UI (`Board.razor` `MoveCardAsync`/`PromoteCardAsync`) commits in isolation, with a
  live/not-live branch split and a worktree re-read.
- **Pre-fork card seeding** — `RunStepAsync` must `LockCardToBranchAsync` (commit the card to
  the outer branch) *before* `git worktree add -b`, or the forked branch can't see its card.

The decoupling insight: **board access should not depend on the agent's checkout.** A step
session's `Workspace.RepositoryRoot` is actually the *code worktree* (it is built as
`new Workspace(wt.WorktreePath)` with no repo-root arg, so `RepositoryRoot` collapses to
`RootPath`). That is the very reason board tools read the code worktree's board today. Once
the board lives on its own branch in a fixed worktree resolved from the **true main repo
root**, the agent reads/writes the same board regardless of which code branch it is on — and
the entire overlay disappears.

---

## 2. Target design

### 2.1 The orphan branch

`yamca-board` is an orphan branch: its first commit is a fresh root with **no parent**, so the
board lives on a history disconnected from code history inside the same repository.

```
main:        A──B──C──D      (code; zero board files)
yamca-board: X──Y──Z         (board only; no shared ancestor, never merged)
```

It shares one object store, one ref namespace, and one remote with the code branches —
behaving like a separate repo's *history* over one repo's *storage*. Consequence to decide
deliberately: it is **pushable/cloneable by default**; keeping it local is an explicit choice
(see §8). Precedent: `gh-pages`.

### 2.2 On-disk layout (Layout A — chosen)

- The orphan branch's tree holds the **columns at its root**: `10-idea/`, `20-analyze/`,
  `30-implement/`, `40-verify/`, `50-done/`, each with `instructions.md` and card `*.md`.
- yamca mounts a **linked worktree of `yamca-board` at `<RepositoryRoot>/.yamca/board`** — the
  same on-disk path the board has today, so "the board is at `.yamca/board`" stays true. The
  difference is that `.yamca/board` is now a *worktree checkout of the orphan branch*, not
  tracked content on the current branch.
- `.yamca/board` (and `.yamca/worktrees`) are **gitignored on code branches**, so the worktree
  checkouts never show up as untracked clutter.

> Layout B (columns nested under `.yamca/board` inside the orphan branch, worktree mounted at
> `.yamca/board-worktree`) leaves `BoardService` literally untouched but changes the familiar
> on-disk path and adds redundant nesting. We pick **A** for the unchanged on-disk location;
> the `BoardService` change it requires is a one-liner (§4.1).

### 2.3 Board location is repo-anchored, not session-anchored

A new singleton, `BoardWorktree`, owns board location and bootstrap. It depends on the
**root** `IWorkspace` singleton (whose `RepositoryRoot` is the true main repo top-level
discovered in `Program.cs` at startup), **not** the per-session workspace. So a step session
bound to a code worktree still resolves the one canonical board. This is the linchpin that
makes the overlay unnecessary.

### 2.4 Commit model and code↔status association

- Every board mutation (new card, move, update, bind) is written into the board worktree and
  **committed to `yamca-board`** immediately. Board commits never touch code branches.
- Because the board worktree contains *only* board files, board commits use a simple
  `git -C <board> add -A && git commit` — no pathspec scoping, no `git mv` rename dance, no
  untracked-fallback. (`MoveWithUntrackedFallbackAsync` / `CommitStagedPathsAsync` /
  `CommitPathsAsync` are no longer needed by the board; see §4.2 / §9.)
- **Association stamp (recovers the lost coupling as a reference):** when `board_move_card`
  runs inside a step session, it reads the *code* worktree's current HEAD
  (`context.Workspace.RootPath`, best-effort `git rev-parse HEAD` + branch) and records it:
  - in the board commit message as a trailer: `Code: <branch>@<sha>`, and
  - in the card frontmatter as `commit: <sha>` (last code commit associated with the card).
  This keeps "what code corresponds to this status change" answerable without co-committing.
  Best-effort: a plain (non-worktree) chat session or detached/empty HEAD simply skips the
  stamp.
- The `branch:` frontmatter is **retained** — it still names the card's code branch and drives
  the dialog's merge / open-chat affordances. It no longer drives any board-status overlay.

### 2.5 Concurrency

The single board worktree replaces per-branch board isolation, so concurrent writers (multiple
chat sessions + the board UI) must serialize. `BoardWorktree` holds a process-wide
`SemaphoreSlim(1,1)`; all mutations (move/update/new/bind/init) take it around the
write-then-commit. Reads are lock-free filesystem reads. Adequate for a local, single-user
tool; revisit only if board writes ever go multi-process.

---

## 3. New components

### 3.1 `BoardWorktree` (new singleton — `Yamca.Agent/Board/BoardWorktree.cs`)

Responsibilities:

- `Task<string> EnsureAsync(CancellationToken)` → returns the board worktree path
  (`<mainRepoRoot>/.yamca/board`), creating it on first use:
  1. If `refs/heads/yamca-board` is missing → create the orphan branch + worktree
     (`GitService.AddOrphanWorktreeAsync`, §4.2), then seed default columns (§3.2) and commit.
  2. Else if the worktree path is not registered (`git worktree list`) → `git worktree add
     <path> yamca-board`.
  3. Else → return the existing path.
  Idempotent; cached after first success.
- `Task<T> MutateAsync<T>(Func<string, Task<T>> action, CancellationToken)` — acquires the
  semaphore, ensures the worktree, runs `action(boardPath)`, releases. All board writes go
  through this.
- Anchors at the main repo root via the injected **root** `IWorkspace` (not a session one).

Anchoring note: when invoked from code that only has a per-session `IWorkspace` (a linked
worktree), do **not** use its `RepositoryRoot`. `BoardWorktree` is a singleton holding the
root workspace, so tools/UI just call the shared instance.

### 3.2 Default columns (relocate)

Move `Board.razor`'s `DefaultColumns` array into a shared constant
(`BoardService.DefaultColumns` or a small `BoardInitializer`) so both the orphan-branch seed
(§3.1) and any UI "initialize" path use one definition. Columns + (possibly empty)
`instructions.md` exactly as today: `10-idea`(rest) → `20-analyze` → `30-implement` →
`40-verify` → `50-done`(rest).

---

## 4. Changed components

### 4.1 `BoardService` (`Yamca.Agent/Board/BoardService.cs`)

- **`BoardDirectory(string boardRoot)`** → return `boardRoot` directly (columns sit at the
  worktree root under Layout A) instead of `Path.Combine(root, ".yamca", "board")`. All callers
  now pass the board worktree path from `BoardWorktree.EnsureAsync`.
- **Delete `ReadForDisplay`** and its worktree-snapshot machinery (the single biggest deletion).
  Callers switch to `Read(boardRoot)`.
- Keep `Read`, `ParseCard`, `WithBranch`, `NextCardId`, `CardFileName`, `PresumptiveBranch`,
  `ReadInstructions`, `HasInstructions`, subtask parsing — unchanged except they now receive the
  board worktree path.

### 4.2 `GitService` (`Yamca.Agent/Git/GitService.cs`)

Add:

- `AddOrphanWorktreeAsync(repoRoot, worktreePath, branch, ct)` — primary:
  `git worktree add --orphan <branch> <worktreePath>` (git ≥ 2.42, creates the orphan inside the
  new worktree without disturbing the main index). Portable fallback when `--orphan` is
  unsupported: plumbing — empty tree via `git mktree </dev/null`, `git commit-tree` with no
  parent, `git update-ref refs/heads/<branch> <commit>`, then `git worktree add <path> <branch>`.
- `CommitAllAsync(worktreePath, message, ct)` — `git -C <path> add -A` then
  `git -C <path> commit -m <message>` (returns `GitResult`; treats "nothing to commit" as a
  benign no-op via `HasUncommittedChangesAsync`-style guard). Used only for the board worktree,
  whose contents are exclusively board files.
- `RevParseHeadAsync(path, ct)` → `(sha, branch?)` for the association stamp (best-effort).
- `BranchExistsAsync(repoRoot, branch, ct)` (or reuse `ListBranchesAsync`).

Remove from board callers (keep only if still used elsewhere — they are not, post-refactor):
`MoveWithUntrackedFallbackAsync`, `StagedMove`, `CommitStagedPathsAsync`, `CommitPathsAsync`,
`MoveAsync`, `AddAsync`. Confirm with a usage sweep before deleting (§9).

### 4.3 Board tools (`Yamca.Agent/Tools/Board/*`)

All five gain a `BoardWorktree` dependency and resolve the board root through it instead of
`context.Workspace.RepositoryRoot`:

- `BoardListTool`, `BoardGetCardTool`, `BoardGetStepInstructionsTool` — read-only: swap
  `Read(context.Workspace.RepositoryRoot)` → `Read(await _boardWorktree.EnsureAsync(ct))`.
  (`BoardGetStepInstructionsTool.ExecuteAsync` becomes genuinely async.)
- `BoardUpdateCardTool` — write the card file then **commit to the board branch** via
  `_boardWorktree.MutateAsync(... GitService.CommitAllAsync ...)`. Update the description:
  drop "This writes the working tree only; commit it with your related changes" → "Saved and
  committed to the board branch."
- `BoardMoveCardTool` — move the card file within the board worktree, **commit to the board
  branch**, and apply the association stamp (§2.4). Update description: drop the "staged but NOT
  committed — bundle into the commit that completes the work" language → describe the
  auto-commit + association. Drop `SupportsWorkspaceRestriction` board-above-sandbox comment
  rationale only where it no longer applies (the board path now comes from `BoardWorktree`).

### 4.4 `BoardPrompts` (`Yamca.Agent/Board/BoardPrompts.cs`)

`BuildSeedPrompt`: replace the "stage your code changes together with the card move so they
land in a single commit" guidance with the new flow — e.g. *"Commit your code changes on this
branch, then move the card to \"{next}\" with board_move_card (the board is tracked separately;
the move records your latest commit) and tick finished subtasks with board_update_card."*

### 4.5 `Board.razor` (`Yamca.Web/Components/Pages/Board.razor`)

- `RefreshAsync`: `_snapshot = BoardSvc.Read(await BoardWorktree.EnsureAsync(ct))`. Still call
  `ListWorktreesAsync` to populate `_branchWorktrees`, but it now only feeds **code-branch
  liveness** affordances (merge / open-chat in the dialog), not board rendering. Rename/redoc to
  make that scope clear.
- `MoveCardAsync` / `PromoteCardAsync`: collapse to a single path — move the card file in the
  board worktree and commit to `yamca-board` via `BoardWorktree.MutateAsync`. **Delete** the
  live/not-live split and the worktree re-read (`BoardSvc.Read(worktreePath).FindCard` etc.).
- `LockCardToBranchAsync`: simplify to "write `branch:` frontmatter + commit to the board
  branch." **Remove** the commit-to-outer-branch-before-fork logic and its
  `HasUncommittedChanges`/`CommitPathsAsync` calls.
- `RunStepAsync`: step 1 ("bind + commit card to outer branch before forking") shrinks to just
  the binding commit on the board branch. Step 2 (`ResolveWorktreeForBranchAsync`) no longer
  needs the card present on the forked branch — the code worktree is a plain fork off the base
  branch; the agent reads the card from the board worktree via board tools.
- `InitializeBoardAsync`: replace direct `.yamca/board` directory writes with
  `BoardWorktree.EnsureAsync` (which seeds default columns on first creation). The empty-board
  view's copy updates accordingly.
- `AddCardAsync`: write the new card into the board worktree and commit to `yamca-board` (today
  it writes the file and relies on a later commit; now it commits immediately like the other
  mutations).

### 4.6 `Program.cs` (`Yamca.Web/Program.cs`)

- Register `builder.Services.AddSingleton<BoardWorktree>();`.
- Optionally call `EnsureAsync` at startup (after the repo-root probe) so the board worktree is
  ready before first navigation; or leave it lazy (first board access creates it). Lazy is
  simpler and avoids startup cost when the board is unused — **prefer lazy**.

### 4.7 `BoardStepLauncher` / `StepRunRequest` / `ChatSessionManager`

No functional change — they still provision the **code** worktree session. They do not read the
board. Confirm no compile coupling to deleted board members.

---

## 5. `.gitignore` and repository hygiene

- Add to the repo's root `.gitignore` (on `main`): `.yamca/board/` and `.yamca/worktrees/`.
- The board worktree's own checkout (the orphan branch) should ignore nothing special; it
  contains only columns.

---

## 6. Migrating yamca's own board (this repo)

yamca dogfoods its board (recent `board: …` commits). One-time migration, scriptable:

1. From a clean `main`, snapshot current board content: `git mv`/copy `.yamca/board/*` aside.
2. Create the orphan branch with the existing columns:
   `git worktree add --orphan yamca-board .yamca/board-tmp`, copy the column tree in, commit.
3. Remove the board from code-branch tracking: `git rm -r --cached .yamca/board` on `main`,
   add the `.gitignore` entries (§5), commit ("board: move board onto orphan branch").
4. Re-mount at the canonical path: remove `.yamca/board-tmp`, `git worktree add .yamca/board
   yamca-board`.
5. Verify `git status` on `main` is clean (board worktree ignored) and the board renders.

This work happens on `board-orphan-experiment` and is part of the deliverable.

---

## 7. Tests

`Yamca.Agent.Tests` (and any web-testable seams):

- **Delete** `ReadForDisplay` tests in `Board/BoardServiceTests.cs`; keep/extend `Read`,
  `ParseCard`, `WithBranch`, ordering tests (now fed a board-root path).
- **New** `Board/BoardWorktreeTests.cs`:
  - bootstrap creates the orphan branch (no parent: `git rev-list --count HEAD` == 1 initially;
    `git log yamca-board --format=%P` first commit has empty parent),
  - seeds default columns,
  - is idempotent (second `EnsureAsync` no-ops, single worktree registered),
  - `MutateAsync` serializes (semaphore) and commits each mutation.
- **New** `Git/GitServiceTests.cs` cases: `AddOrphanWorktreeAsync` (both `--orphan` and the
  plumbing fallback if feasible to force), `CommitAllAsync` (commits; no-op when clean),
  `RevParseHeadAsync`.
- **Update** `Tools/Board/BoardToolsTests.cs`: move/update now commit to the board branch;
  `board_move_card` writes the association stamp (assert frontmatter `commit:` and the
  `Code: …` commit trailer when a code HEAD exists; assert it is skipped when none).
- **Update** `Board/BoardPromptsTests.cs` for the new seed-prompt wording.
- Razor (`Board.razor`) is not unit-tested directly; rely on the extracted service tests +
  a manual `/verify` pass (drag a card, run a step, confirm one board commit per move and a
  clean code-branch `git status`).

---

## 8. Open decisions (resolve during execution)

| Decision | Default to take | Note |
|---|---|---|
| Push semantics | **Local-only**: do not push `yamca-board` by default | Add an explicit opt-in later; document that the board branch is a separate shareable artifact (ties into the future chat-history-orphan idea). |
| Association stamp format | **Both** frontmatter `commit:` + commit-message `Code:` trailer | Frontmatter is queryable card state; trailer keys board history to code history. |
| git version floor | Use `worktree add --orphan` (≥ 2.42) with plumbing fallback | Detect once; pick fallback if `--orphan` errors. |
| Startup vs lazy worktree creation | **Lazy** (first board access) | Avoids cost when board unused; §4.6. |
| `branch:` on cards | **Keep** (drives merge/open-chat); decoupled from rendering | §2.4. |

---

## 9. Execution order (checklist)

1. [ ] `GitService`: add `AddOrphanWorktreeAsync`, `CommitAllAsync`, `RevParseHeadAsync`,
   `BranchExistsAsync` (+ tests).
2. [ ] Add `BoardWorktree` singleton (+ tests); relocate `DefaultColumns` to a shared constant.
3. [ ] `BoardService`: change `BoardDirectory`, delete `ReadForDisplay` (+ adjust tests).
4. [ ] Board tools: inject `BoardWorktree`; reads via `EnsureAsync`; move/update commit to the
   board branch; move applies the association stamp; update descriptions.
5. [ ] `BoardPrompts`: new seed-prompt wording (+ test).
6. [ ] `Board.razor`: rewrite `RefreshAsync`, `MoveCardAsync`/`PromoteCardAsync`,
   `LockCardToBranchAsync`, `RunStepAsync` step 1, `InitializeBoardAsync`, `AddCardAsync`.
7. [ ] `Program.cs`: register `BoardWorktree` (lazy).
8. [ ] Usage sweep: delete now-dead `GitService` board helpers
   (`MoveWithUntrackedFallbackAsync`, `StagedMove`, `CommitStagedPathsAsync`, `CommitPathsAsync`,
   `MoveAsync`, `AddAsync`) once confirmed unreferenced.
9. [ ] `.gitignore` entries; migrate this repo's board onto `yamca-board` (§6).
10. [ ] `dotnet build` + `dotnet test`; manual `/verify` (drag a card, run a step end-to-end,
    confirm one board commit per move and a clean code-branch `git status`).

---

## 10. Net assessment

The dedicated-branch model is the better default for a local dev tool: it deletes the single
largest complexity sink (`ReadForDisplay` + overlay), removes the frozen-card problem, ends
board-vs-code merge conflicts, and unifies the move primitive — at the cost of atomic
code+status commits, which we recover as a *reference* via the association stamp. The new
concern (single-worktree write contention) is handled by an in-process lock and is acceptable
for the single-user case.

## 11. Out of scope (future conversation)

Storing **chat history** in its own orphan branch / worktree(s), kept local and unpushed by
default, as a way to share chats with teammates — same orphan-branch pattern, separate work.
