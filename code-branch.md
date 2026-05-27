# Code Branching Feature

Add a "Branch Code" action to the chat composer toolbar that creates (or attaches to) a git worktree and spawns a fresh chat session bound to that worktree's directory. Sessions tied to a worktree expose merge / delete-branch actions on the same toolbar.

## Goals

1. From any chat session, the user can click a branch icon, name a branch (or pick an existing one), and get a new chat session whose working directory is a freshly created git worktree.
2. Worktree-bound sessions show two extra toolbar buttons: **Merge** and **Delete**, both with confirmation/options modals.
3. No automatic cleanup — worktrees and branches persist until the user deletes them explicitly.
4. Permissions in the worktree session match the original session because permissions are scoped "inside the working directory" (no absolute paths). Registered scripts use relative paths and apply unchanged.

## Non-goals (v1)

- No UI to list/manage orphaned worktrees beyond what an individual session owns.
- No support for branching when the workspace is not a git repo (button is disabled with a tooltip).
- No conflict-resolution UI for merges — if `git merge`/`git rebase` reports conflicts, surface the stderr and instruct the user to resolve in their editor.
- No cross-session awareness: closing the chat tile does not delete the worktree, and reopening Yamca later does not auto-reattach. (The worktree is still on disk and can be reattached via a future "open existing worktree" flow.)

## User flow

### Creating a branch

1. User clicks the branch icon (between the attach button and the context badge in `Yamca.Web/Components/Chat/ChatSessionPanel.razor`).
2. A MudBlazor dialog opens with:
   - A text field for **New branch name** (validated against `git check-ref-format`).
   - A combobox of **existing local branches** (excluding the current HEAD of the source workspace). Selecting one switches the dialog into "attach to existing branch" mode and disables the name field.
   - A read-only label: "Base: `<current branch of source workspace>`" (only shown in new-branch mode).
   - **Create** / **Cancel** buttons.
3. On Create:
   - Compute worktree path: `<repo>/.yamca/worktrees/<sanitized-branch>` (sibling-style would be cleaner but stays scoped to the repo for easier cleanup; sanitize `/` → `-`).
   - New branch: `git worktree add <path> -b <branch>` (bases off current HEAD of the source workspace by default — that matches the user's expectation).
   - Existing branch: `git worktree add <path> <branch>`. If the branch is already checked out elsewhere, fail with a clear error.
4. On success, create a new `ChatViewModel` via `ChatSessionManager.CreateForWorktree(...)` (see below) and navigate to it. The original session stays intact.
5. The new session starts empty (fresh `ChatSession`, fresh system prompt re-rendered against the worktree). No auto-seeded "the working directory has changed" message — the session simply *starts* in the worktree, which is the cleanest semantics. The tile header shows the branch name (see Display).

### Merging

Only visible when `ChatViewModel.WorktreeInfo is not null`.

1. User clicks the **Merge** icon. Dialog opens with:
   - Read-only "From `<branch>` → `<base-branch>`".
   - Radio: **Merge strategy** — `Merge commit` / `Rebase` / `Squash`.
   - Checkbox: **Delete branch & worktree after merge** (default on).
   - **Merge** / **Cancel** buttons.
2. On confirm:
   - Run the chosen git operation in the **base** worktree (the original session's directory), not in the worktree session. This is safer because it doesn't require the worktree session to be idle.
     - Merge commit: `git -C <base> merge --no-ff <branch>`
     - Rebase: `git -C <base> rebase <branch>` (fast-forwards if linear)
     - Squash: `git -C <base> merge --squash <branch> && git -C <base> commit -m "Squash merge of <branch>"`
   - On conflict, surface stderr in an error dialog and abort cleanup. The branch and worktree remain.
   - On success and if cleanup checked: `git worktree remove <path>` then `git branch -d <branch>`. Then close the worktree-bound chat session.

### Deleting

Only visible when `ChatViewModel.WorktreeInfo is not null`.

1. User clicks the **Delete** icon. Dialog opens:
   - Body: "Delete branch `<branch>` and its worktree?"
   - If `git log <base>..<branch>` is non-empty, prepend a warning: "This branch has N unmerged commit(s). They will be lost."
   - **Delete** / **Cancel**.
2. On confirm: `git worktree remove --force <path>` then `git branch -D <branch>`. Then close the chat session.

## Architecture changes

### 1. Make `IWorkspace` per-session, not singleton

`Yamca.Web/Program.cs:75` currently registers `IWorkspace` as a singleton. The agent loop, tools, and instruction loader take `IWorkspace` from DI. To support per-session workspaces, switch registration:

- Keep the process-bound `Workspace` as a singleton under a new name (`RootWorkspace`) — this is the workspace the app launched into and is the default for new sessions.
- Register `IWorkspace` as **scoped**, resolving from a new per-circuit `WorkspaceProvider` that defaults to the root and can be reassigned when a session is bound to a worktree.

But the cleaner path — and the one I recommend — is to stop injecting `IWorkspace` into `ChatViewModel` from DI and instead pass it as a constructor parameter, the way `Id` already is. `ChatSessionManager.Create()` already uses `ActivatorUtilities.CreateInstance<ChatViewModel>(_services, id)`; we extend it to `CreateForWorktree(IWorkspace worktreeWorkspace, WorktreeInfo info)` which passes the worktree workspace as an extra param. The remaining DI-injected `IWorkspace` consumers (`Home.razor`, `Settings.razor`, `ApprovalPrompt.razor`, `WorkspaceBrowser`) all want the *root* workspace, so they stay on the singleton.

`ChatViewModel.EnsureStarted` already constructs `ToolContext` with `new ToolContext(_workspace, ...)` per tool, and the `AgentLoop` ctor takes `_workspace` too. So once `_workspace` is per-session, every tool call inside that session automatically resolves paths against the worktree.

**Files affected:**
- `Yamca.Web/Services/ChatViewModel.cs` — accept `IWorkspace` as ctor param (no DI keyword change), accept optional `WorktreeInfo`.
- `Yamca.Web/Services/ChatSessionManager.cs` — add `CreateForWorktree(IWorkspace, WorktreeInfo)`; existing `Create()` keeps using the DI-resolved root workspace.
- `Yamca.Web/Program.cs` — no DI changes required (workspace stays a singleton; `ChatViewModel` just stops resolving it from `ActivatorUtilities` when we pass an override).

### 2. New `WorktreeInfo` record

```csharp
public sealed record WorktreeInfo(
    string Branch,        // "feature/login"
    string BaseBranch,    // "main" — the branch checked out in the source workspace at create time
    string WorktreePath,  // absolute, canonicalized
    string BasePath);     // absolute root path of the source workspace (for running merge ops)
```

Held by `ChatViewModel` as a nullable property. Drives the visibility of the Merge/Delete toolbar buttons and the tile header label.

### 3. New `GitService`

Stateless service that wraps `git` invocations. Returns a result record with stdout/stderr/exit code rather than throwing on non-zero exit. Methods:

- `Task<bool> IsGitRepoAsync(string path, CancellationToken ct)`
- `Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct)` — null in detached HEAD.
- `Task<IReadOnlyList<string>> ListBranchesAsync(string path, CancellationToken ct)` — local branches only.
- `Task<GitResult> CreateWorktreeAsync(string repoPath, string worktreePath, string branch, bool isNewBranch, CancellationToken ct)`
- `Task<GitResult> MergeAsync(string repoPath, string branch, MergeStrategy strategy, CancellationToken ct)`
- `Task<GitResult> RemoveWorktreeAsync(string worktreePath, bool force, CancellationToken ct)`
- `Task<GitResult> DeleteBranchAsync(string repoPath, string branch, bool force, CancellationToken ct)`
- `Task<int> CountCommitsAheadAsync(string repoPath, string baseBranch, string branch, CancellationToken ct)` — for the unmerged-changes warning.

Registered as a singleton in `Program.cs`. Uses `System.Diagnostics.Process` directly; no submodule support needed.

`GitResult` is `record GitResult(int ExitCode, string Stdout, string Stderr) { public bool Ok => ExitCode == 0; }`.

### 4. Toolbar UI

Edit `Yamca.Web/Components/Chat/ChatSessionPanel.razor`:

- Inject `GitService` and `ChatSessionManager`.
- Add three new icon buttons inside `<div class="yamca-composer-left">`, after the attach button:
  - **Branch** (`Icons.Material.Filled.CallSplit`, tooltip "Branch Code") — always rendered; disabled when the workspace isn't a git repo (probe lazily on first render and cache).
  - **Merge** (`Icons.Material.Filled.Merge`, tooltip "Merge branch") — only rendered when `Chat.WorktreeInfo is not null`.
  - **Delete branch** (`Icons.Material.Filled.DeleteForever`, tooltip "Delete branch & worktree") — same visibility.
- Show worktree info in the tile header (when `ShowHeader`), e.g. append `· <branch>` after `TileTitle` so the user can tell tiles apart in the split view.

### 5. Dialogs

Three new MudBlazor dialogs under `Yamca.Web/Components/Branching/`:
- `BranchCreateDialog.razor` — name field, existing-branch combobox, base label.
- `BranchMergeDialog.razor` — strategy radio, delete-after checkbox.
- `BranchDeleteDialog.razor` — confirmation with optional unmerged-commits warning.

Each dialog calls `GitService`, surfaces errors inline, and only closes on success.

### 6. Session lifecycle

When a worktree-bound session is closed via the existing `MudIconButton Close` in the tile header, the worktree is **not** removed. The user can recreate the session later (future work: "reopen worktree" picker). On Manager `Dispose` (browser tab closes), same: worktrees outlive the circuit.

For v1 there is no persistence of `WorktreeInfo` to localStorage — closing the tab loses the binding. That's acceptable because Yamca currently doesn't persist sessions either. If/when session persistence is added, `WorktreeInfo` becomes part of the saved session shape.

## Edge cases

- **Workspace is not a git repo.** Branch button is disabled with tooltip "Not a git repository". Probe once via `GitService.IsGitRepoAsync` and cache on the `ChatViewModel`.
- **User picks a branch that is already checked out in another worktree.** `git worktree add` fails; show stderr in the dialog.
- **Branch name collides on create.** `git worktree add -b` fails; surface stderr.
- **Base workspace HEAD moves between branch creation and merge.** The merge runs `git merge <branch>` in the base workspace's current branch, which may differ from `BaseBranch` recorded at create time. Show the current base branch in the merge dialog (recomputed at dialog open), not the cached one.
- **Dirty worktree on delete.** `git worktree remove --force` handles it; the confirmation already warned the user about unmerged commits, which is the data-loss case worth flagging.
- **Permissions registered against the base session apply in the worktree session** because `PermissionResolver` consults `SessionSettings`, which is scoped per-circuit. Both sessions live in the same circuit and share the same `SessionSettings`. That matches the user's stated requirement; verify by reading [[SessionSettingsPermissionStore]] and [[PermissionResolver]] during implementation.
- **Concurrent operations.** Disable Merge/Delete buttons while `Chat.IsRunning` on either the worktree session *or* the base session (the base session may be modifying files we're about to merge). Look up the base session by matching `WorktreeInfo.BasePath` against `Manager.Sessions[].Workspace.RootPath`.

## Test plan

Unit:
- `GitService` against a temp repo created in `Yamca.Agent.Tests` — covers create / merge (all three strategies) / remove / branch-format validation / detached-HEAD handling.
- `ChatSessionManager.CreateForWorktree` returns a session whose `_workspace.RootPath` is the worktree path.

Manual / integration:
- Create a branch from `main`, edit a file in the worktree session via a tool call, verify the change lands in the worktree and not the base.
- Merge with each strategy back to `main`, with cleanup on and off.
- Delete with and without unmerged commits — confirm the warning fires correctly.
- Open the worktree session in the split view alongside the base session and confirm both work concurrently.

## Out of scope (future)

- Persisting and reattaching worktree-bound sessions across app restarts.
- A "worktrees" picker showing all worktrees of the current repo (whether or not they have an open session).
- Pushing the branch to a remote from the toolbar.
- Pull-request creation hooks.
