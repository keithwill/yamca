# Diff & File-Change Visibility — Design Plan

Status: proposal · Target: yamca web UI (Blazor Server)

## Goal

Give users visibility into (and interaction with) the file changes that AI work produces in
card-bound worktrees — and into the edits an agent makes live during a chat session. Do this with
portable, cross-platform dependencies only (no secondary install).

## Context (why this fits yamca)

- The UI is **Blazor Server** (Razor components + SignalR). Diffs are computed server-side and
  pushed to the browser — no client-side JS diff library is needed, and live-updating diffs ride
  the existing SignalR channel for free.
- `GitService` already **shells out to `git`** (`status --porcelain`, `show`, `log`, worktree ops)
  and the app already depends on a `git` binary being present (worktrees require it). There is
  currently **no `diff` wrapper**.
- **Cards bind to a `Branch` + a code worktree** at `.yamca/worktrees/NNNN-slug`. The agent works
  there via a `ChatViewModel` (`BoardStepLauncher` → `ChatSessionManager.CreateForWorktree`), and
  `Edit`/`Write` tools mutate files in that worktree. yamca therefore already knows exactly which
  worktree each card's work lands in — the natural anchor for every diff surface.

## Architectural principle: `git diff` and DiffPlex are complementary

They answer different questions; use each for its job.

| Question | Tool | Why |
|---|---|---|
| Which files changed, and by how much? | `git diff --numstat` / `--name-status` | Authoritative; respects `.gitignore`; detects renames; free (git already a dependency) |
| Show the committed branch-vs-base change | `git diff <base>...<branch>` | Git is the source of truth for committed/on-disk state |
| Render a clean side-by-side / inline model from two strings | **DiffPlex** | `SideBySideDiffBuilder.Diff(old, new)` beats hand-parsing git's unified output |
| Highlight what changed **within** a line | **DiffPlex** (word/char diff) | git `--word-diff` is painful to parse; DiffPlex yields typed sub-line segments |
| Diff a change not yet on disk (a tool's `old_string`→`new_string`, or a Write pending approval) | **DiffPlex** | git can't — the change is in memory |

**Summary:** git enumerates changes and supplies before/after text; DiffPlex turns text pairs into
render models. This yields a single rendering path (the DiffPlex model) for both committed and
in-memory diffs, and DiffPlex only earns its place once we reach the in-memory / intra-line cases
git cannot serve.

### Dependency

- **DiffPlex** (core package only). Pure managed C#, .NET Standard target, no native binaries, no
  P/Invoke, AOT-friendly, Apache-2.0. Produces diff *models* (`SideBySideDiffModel`,
  `DiffPaneModel` with per-line/sub-line `ChangeType`); we render them ourselves in Razor. Do **not**
  pull in `DiffPlex.Wpf` or HTML-renderer companions — we own the rendering.

## Phases

The phasing deliberately defers the DiffPlex dependency until the phase that actually requires it.
Each phase ships standalone value.

### Phase 0 — Git plumbing (no new dependency)

Extend `GitService` with the read-only diff primitives every later phase consumes:

- `--numstat` / `--shortstat` → added/removed line counts.
- `--name-status` → changed file list with status (A/M/D/R) against a base ref and for the working tree.
- Before/after blob retrieval: base blob via `git show <ref>:<path>` (extends existing
  `ShowFileAtParentAsync`), current text from the working copy or `git show :<path>`.
- Merge-base resolution for "branch vs where it forked" (`git merge-base`).
- Binary detection + a size cap, so later phases can skip files before handing text to DiffPlex.

Deliverable: tested `GitService` methods. No UI yet.

### Phase 1 — Working-tree status badge (no DiffPlex)

Ambient awareness, proves the Phase 0 plumbing.

- On each board card and `ChatSessionPanel`, a small chip: e.g. `5 files · +120 −34 · 2 uncommitted`,
  from `git diff --shortstat` + `status --porcelain`.
- Drives attention to cards with pending work and makes the later Changes tab discoverable.

Deliverable: status chip wired to live session/card state over SignalR.

### Phase 2 — DiffPlex + reusable `<DiffView>`, landed on live tool-call diffs

This is the uniquely yamca-valuable case git cannot do, so it justifies adding the dependency.

- Add the **DiffPlex** package.
- Build **one reusable `<DiffView>`** Razor component taking a DiffPlex model
  (`SideBySideDiffModel` / `DiffPaneModel`). Features: inline ⇄ side-by-side toggle, per-file
  collapse, sticky file header, word-level intra-line highlighting, collapsed unchanged context with
  "expand ± N lines", and `Virtualize` over the line list so large files don't bloat the SignalR DOM.
- Render it in `ToolCallCard.razor`: when a tool is `Edit`/`Write`, show the actual change
  (`old_string`→`new_string`, or full before/after) as a diff inline in the conversation, streaming
  live. The change is in memory — no disk round-trip, no commit.
- Integrate with the existing `ApprovalPrompt`: gate approval on the visible diff
  ("apply this change? [diff]").

Deliverable: DiffPlex dependency, `<DiffView>` component, live tool-call diffs + approval-gated diffs.

### Phase 3 — Per-card "Changes" tab (the headline board feature)

Directly answers "let the user see diffs from files modified in the worktree."

- Add a **Changes** tab to `CardDetailDialog.razor` (the card already knows its branch/worktree).
- File list from `git diff --name-status <merge-base>...HEAD` plus working-tree changes; click a file
  → `<DiffView>` rendering base-blob vs current.
- Distinguish committed vs uncommitted (separate sections or a per-file badge).

Deliverable: per-card changes review built entirely on the Phase 2 component.

### Phase 4 — Pre-merge review gate

- Extend the existing `BranchMergeDialog.razor`: before merging a card's branch back, show the full
  branch-vs-base diff as a review step — the human-approves-the-AI's-work checkpoint.
- Reuses the exact `<DiffView>` from Phases 2–3.

Deliverable: diff-backed merge confirmation.

### Phase 5 — Card-content history diffs (optional, low cost)

- The board is itself git (orphan branch); restore plumbing already exists
  (`GetDeletedFilesAsync` / `ShowFileAtParentAsync`).
- Diff a card's markdown across its own history ("what changed in this card's spec") — the same
  DiffPlex-on-two-`git show`-outputs pattern.

Deliverable: card history diff view, free once `<DiffView>` exists.

## Cross-cutting interactivity notes (Blazor Server)

- A single `<DiffView>` is the rendering contract for every surface above.
- Inline ⇄ side-by-side toggle; per-file collapse; sticky file headers.
- Word/char-level intra-line highlighting (DiffPlex) — the polish that makes diffs readable.
- Collapsed unchanged context with on-demand expansion.
- `Virtualize` the line list to keep large files from blowing up the SignalR DOM diff.
- Guards: lean on git's binary detection + a size cap to skip binary/huge files before DiffPlex.

## Sequencing rationale

1. Phase 0 + 1 ship value with zero new dependencies and de-risk the git plumbing.
2. The DiffPlex dependency is only committed in Phase 2, at the first case that genuinely needs it.
3. Phases 3–5 are reuse of one component — incremental surface area, no new core work.
