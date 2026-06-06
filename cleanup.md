# Codebase cleanup backlog

A scan of the codebase for architectural, maintenance, and performance cleanups.
Items are roughly ordered by value. Each is independent and can be done on its own
branch with tests.

The codebase is in good shape overall — no TODOs, no swallowed-error spam, consistent
`ConfigureAwait`, good XML docs. These are concentrated hotspots, not pervasive rot.

---

## Maintenance

### 1. Duplicated LLM HTTP-client construction (4+ call sites)

The same "endpoint → `HttpClient` → `OpenAIChatCompletionClient`" boilerplate
(base-URL slash normalization, `Bearer` auth header, `Timeout.InfiniteTimeSpan`, the
`"yamca-llm"` named client) is copy-pasted across:

- `Yamca.Web/Services/ChatViewModel.cs:479-489` (`EnsureStarted`)
- `Yamca.Agent/Subagents/SubagentRunner.cs:284-297` (`BuildClient`)
- `Yamca.Web/Services/ContextCompactor.cs:69-74`
- `Yamca.Agent/Chat/EndpointHealthService.cs:64`, `:118`, `:157`

The slash-normalization (`baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"`) alone is
open-coded in 5 places.

**Fix:** an `EndpointClientFactory` (or an `EndpointSettings` extension such as
`CreateCompletionClient(IHttpClientFactory)` / `CreateConfiguredClient(...)`). Highest-leverage
item — today endpoint/auth behavior can silently drift between the main chat, subagents,
compaction, and health probing. Also unblocks item #4.

### 2. `SessionSettings.cs` (805 lines) — pervasive internal duplication

Adding one setting currently requires ~4 coordinated edits. Three kinds of repetition:

- **Default constants** (`75`, `4`, ranges `1..95` / `1..50`, per-tool defaults) repeated
  across property initializers, `ResetUserToDefaults` (`:218-225`), `HydrateUser`
  (`:315-325`), and `ApplyUserBlob` (`:527-537`).
- **`HydrateUser` vs `ApplyUserBlob`** are near-identical blob→field mappers
  (`:308-332` vs `:524-542`), differing only in first-run handling. One should delegate
  to the other.
- **`SerializeUser` vs `ExportUser`** build the same `UserBlob` literally twice
  (`:429-445` vs `:469-484`).

**Fix:** extract a single `ApplyBlob(UserBlob, bool firstRun)` and a single `ToUserBlob()`
builder; hoist magic numbers to named constants. Removes the "added a setting but forgot
to wire it into export" failure mode and trims ~150 lines.

### 3. Symbol-extractor helper duplication (12 files)

Every `*SymbolExtractor` in `Yamca.Agent/Tools/CodeIntel/` re-implements the same small
tree-walking helpers — `FirstDescendant`, `NameOrAnonymous`, `BareName`, and
`GetChildForField("name")` name extraction (66 occurrences across 12 files).
`CSharpSymbolExtractor.cs:114-149` is representative.

**Fix:** extract a static `NodeHelpers` class holding the shared utilities. (Deliberately
*not* an abstract base class — the per-language node-type tables `TryContainer` /
`TryMember` legitimately differ and stay per-class; we only want to share the stateless
tree-navigation helpers.)

---

## Architectural

### 4. `ChatViewModel` constructor takes 15 dependencies — DONE

Introduced `AgentLoopFactory` (`Yamca.Agent/Chat/AgentLoopFactory.cs`) capturing the stable
loop collaborators (tool registry, permission/availability resolvers, approval coordinator,
permission store, loaded tool set); `Create` takes only the per-session pieces (session,
completion client, workspace, options). `ChatViewModel` dropped its three loop-only deps
(`IAvailabilityResolver`, `IPermissionStore`, `LoadedToolSet`), down from 15 to 12. The
endpoint client factory (item #1) stays a direct dep because the completion client is also
bound to the subagent runner, not solely threaded into the loop.

Original note:

`Yamca.Web/Services/ChatViewModel.cs:49-64`. The class is cohesive (per-circuit
orchestrator) so this isn't wrong, but it's at the threshold where it's hard to test and
construct. Several deps exist only to build the `AgentLoop` once. Consider grouping the
loop's collaborators behind an injected `AgentLoopFactory` — which also naturally absorbs
item #1.

### 5. Redundant git invocations for the same worktree — DONE

The "6 processes per render" overstated it: the stat badge (`ChatSessionPanel`, always in
the panel header) and the file list (`WorktreeChangesView`, an on-demand dialog) are
separate render paths, so they never run together. The real redundancy was *within*
`WorktreeChangesView.LoadChangesAsync`, which called `GetMergeBaseAsync` explicitly and then
`GetWorktreeChangesAsync` — which recomputed the same merge-base internally (4 git processes,
merge-base twice). Added `GitService.GetWorktreeChangesFromBasisAsync(worktreePath, basis, ct)`;
the view now resolves the fork point once and shares it for both the change list and the
later "before"-side `ShowFileAtRefAsync`. Down to 3 processes.

Original note:

`GitService.GetWorktreeDiffStatAsync` (`:227`) and `GetWorktreeChangesAsync` (`:274`) each
independently run `merge-base` + `diff` + `status --porcelain`. If the UI renders both the
stat badge and the file list for one worktree (likely), that's 6 git processes where 3
would do. Minor; consider caching merge-base / porcelain status per render.

---

## Performance

### 6. Un-throttled re-render on every stream token

`ChatViewModel.DriveAsync` calls `Raise()` (→ `StateHasChanged`) on **every**
`ChatStreamEvent`, including each content token (`ChatViewModel.cs:287-291`). For a fast
local model this can be hundreds of full-tree renders/sec over SignalR.

**Fix direction:** review the components that render in the chat log area
(`ChatTurnView`, `ChatTurnItem`/`ToolCallCard`, `ReasoningCard`, `ChatSessionPanel`, and
the markdown renderer) and have them implement `ShouldRender` so that unmodified chat-log
items don't re-render on every token — only the turn item currently receiving tokens
should re-render. Measure first (it may be acceptable on localhost), but this is the most
likely perf hotspot.

---

## Notes / verified-clean

Checked and in good shape — no action needed:

- `AgentLoop` — the per-iteration `GetChatTools` rebuild and prompt-prefix cache stability
  are deliberate and well-commented.
- `GitService` process wrapper, `ProcessRunner` output capping, `Program.cs` DI + CLI parsing.
