# yamca — Implementation Progress

Tracks phased implementation of [PLAN.md](./PLAN.md). Designed for `/clear` between phases — a fresh Claude session should be able to resume by reading this plus `PLAN.md`.

## Phase status

| # | Phase | Status | Verification |
|---|---|---|---|
| 1 | Scaffold + Workspace | ✅ done | `dotnet test` — 13 pass, 2 skipped (symlinks) |
| 2 | Tools + Permissions | ✅ done | `dotnet test` — 51 pass, 2 skipped |
| 3 | Chat loop + fake LLM | ✅ done | `dotnet test` — 67 pass, 2 skipped |
| 4 | Blazor shell + Settings page | ✅ done | `dotnet build` clean; 67 tests still pass |
| 5 | Chat page + approval UI | ✅ done | `dotnet build` clean; 67 tests still pass |
| 6 | Smoke against real llama-server | ⏳ next | manual end-to-end |
| 7 | Optional UI polish | optional | manual UI verification |

Each phase ends with a verifiable checkpoint (green tests for 1-3, build + manual for 4+). The "fake LLM" in phase 3 lets the agent loop be fully unit-testable without network.

## Repo state

Layout (flat, no `src/` or `tests/` parent — user merged them):
```
yamca/
  yamca.slnx                  # .NET 10 uses XML solution format
  Yamca.Agent/                # agent core (classlib, net10.0)
  Yamca.Web/                  # Blazor Web App, Interactive Server, --empty template
  Yamca.Agent.Tests/          # NUnit
  PLAN.md
  TODO.md
```

NuGet: `OpenAI 2.10.0` in agent, `MudBlazor 9.4.0` in web. Test project references agent. Web project references agent.

## Phase 1 — Workspace sandbox ✅

`Yamca.Agent/Workspace/` — `IWorkspace`, `Workspace`, `PathOutsideWorkspaceException`.

`Workspace.Resolve(path)` does:
1. Combine with root if relative.
2. `Path.GetFullPath` to resolve `..`.
3. **Walk every existing segment and call `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`** — this is the deviation from PLAN.md, which said "the OS resolves symlinks on access". Symlinks at any depth (including intermediate dirs / Windows junctions) are caught up front, not just at the leaf.
4. `StartsWith(RootPath + DirectorySeparatorChar, OrdinalIgnoreCase)` — the separator suffix defeats the `/foo` vs `/foo-sibling` false positive.
5. Throw `PathOutsideWorkspaceException` on escape.

Tests in `Yamca.Agent.Tests/Workspace/WorkspaceTests.cs`. Symlink tests use `Assert.Ignore` when `Directory.CreateSymbolicLink` fails (requires admin / Developer Mode on Windows).

## Phase 2 — Tools + Permissions ✅

`Yamca.Agent/Tools/`:
- `ITool` — `Name`, `Description`, `ParametersSchema` (JSON Schema as string), `SupportsWorkspaceRestriction`, `DefaultPermission`, `ExecuteAsync(JsonElement args, ToolContext, CancellationToken)`.
- `ToolContext` — `Workspace` + per-call `RestrictToWorkspace` flag.
- `ToolResult` — `(IsError, Content)`; `Ok` / `Error` factories.
- `ToolArguments` — internal helper for string-arg extraction + sandbox-aware path resolution. **All tools should use it** rather than poking at `JsonElement` directly.
- `IToolRegistry` / `ToolRegistry` — registration-order enumeration; produces `OpenAI.Chat.ChatTool[]` via `ChatTool.CreateFunctionTool(name, desc, BinaryData.FromString(schema))`.
- Five tools, each with hardcoded JSON schema string:
  - `ReadFileTool` — default `Allow`, sandboxable
  - `WriteFileTool` — default `Ask`, sandboxable; creates parent dirs
  - `DeleteFileTool` — default `Ask`, sandboxable; **refuses directories explicitly**
  - `ListDirectoryTool` — default `Allow`, sandboxable; directories rendered with trailing `/`
  - `ExecuteCommandTool` — default `Ask`, **not** sandboxable; `cmd.exe /c` on Windows, `/bin/sh -c` elsewhere. 60s default timeout (max 600), output capped at 16K chars per stream. Non-zero exit → `IsError = true`.

`Yamca.Agent/Permissions/`:
- `PermissionLevel { Allow, Deny, Ask }`.
- `IPermissionResolver` / `PermissionResolver` — merges project → global → tool default. Same precedence applies to `RestrictToWorkspace` (default = `SupportsWorkspaceRestriction`).
- `ApprovalPersistence { None, Project, Global }`, `ApprovalDecision`, `ApprovalRequest` (with `Approve()` / `Deny()` methods backed by an internal `TaskCompletionSource`).
- `IApprovalCoordinator` / `ApprovalCoordinator` — `Channel<ApprovalRequest>` (Unbounded, SingleReader). `RequestApprovalAsync` enqueues + awaits; cancellation cancels the TCS via a registration. **The coordinator does NOT persist** — it returns the persistence intent and lets the UI layer (phase 4+) write to localStorage.

`Yamca.Agent/Settings/`:
- `ToolPermissionSettings` — `{ PermissionLevel? Permission, bool? RestrictToWorkspace }`. Nullable = "not set, fall through".
- `ToolSettingsMap` — name → settings dict, with `Empty` singleton.
- `ISessionSettings` — currently exposes only `Project` and `Global` maps. **Phase 4 will extend this** with `EndpointSettings`, system prompt, etc.

Tests in `Yamca.Agent.Tests/Tools/`, `Yamca.Agent.Tests/Permissions/`. Shared helpers in `Support/`:
- `TempWorkspace` — IDisposable; fresh temp dir + bound `Workspace`.
- `InMemorySessionSettings` — settable `Project` / `Global`.
- `Json.Parse(string)` — convenience for inline `JsonElement` literals.

## Phase 3 — Chat loop + fake LLM ✅

`Yamca.Agent/Chat/`:
- `ChatSession` — ordered `List<ChatMessage>` (system at index 0). Second ctor accepts `IWorkspace` + template and substitutes `{{workspace}}`. `AppendAssistant` handles both content-only and content-plus-tool-calls (the latter constructs the assistant message from `IEnumerable<ChatToolCall>` and then appends the text part to `Content`, since OpenAI's 2.x SDK has no single ctor for both).
- `LlmStreamEvent` (LLM-client layer) → `LlmContentDelta` | `LlmAssistantTurnComplete`. The adapter is responsible for aggregating the OpenAI SDK's fragmented `StreamingChatToolCallUpdate` deltas (indexed) into one completed `LlmToolCallRequest` per call before emitting the terminal event. **Keeps fakes trivial** — they just yield finished events.
- `ChatStreamEvent` (UI layer) → `AssistantTokenEvent` | `AssistantMessageEvent` | `ToolCallStartedEvent` | `ToolCallResultEvent` | `ToolDeniedEvent` | `TurnCompleteEvent` with a `TurnCompletionReason` enum (`AssistantReply`, `MaxIterationsReached`, `Cancelled`).
- `IChatCompletionClient.StreamAsync(messages, tools, ct)` — abstraction over `ChatClient.CompleteChatStreamingAsync`. `OpenAIChatCompletionClient` is the production wrapper.
- `AgentLoop.RunTurnAsync(userMessage, ct)` returns `IAsyncEnumerable<ChatStreamEvent>`. Each iteration: stream LLM events, append assistant message, execute tool calls in order, loop. Terminates on plain assistant reply OR when `MaxIterations` is hit (default 10, configurable via `AgentLoopOptions`).

`Yamca.Agent/Permissions/IPermissionStore.cs` — `Persist(toolName, decision, tier)`. The phase 4 UI implementation will mutate `ISessionSettings` + write to localStorage. The test impl (`Yamca.Agent.Tests/Support/InMemoryPermissionStore`) mutates the in-memory settings so the resolver sees the new value on the *next* tool call within the same turn — that's how the "approve once, persist" tests verify the second call doesn't re-prompt.

`ISessionSettings` extended:
- `EndpointSettings Endpoint { get; }` (record: BaseUrl/ApiKey/Model, with `Default` pointing at `http://localhost:8080/v1`)
- `string SystemPrompt { get; }`

**Constraint discovered**: C# iterators (`async IAsyncEnumerable`) forbid `yield return` inside `catch` clauses (CS1631) and inside `try` blocks that have `catch` (CS1626). So in `AgentLoop.HandleToolCallAsync`, JSON parsing and tool execution are wrapped in **non-iterator helpers** (`TryParseArguments`, `ExecuteToolSafelyAsync`) that return result tuples / `ToolResult.Error(...)`. The iterator method only yields after the helper returns.

`Yamca.Agent.Tests/`:
- `Support/FakeChatCompletionClient` — `EnqueueText(content)` / `EnqueueToolCall(callId, name, args)` / `Enqueue(ScriptedResponse)`. Records every `StreamAsync` invocation.
- `Support/InMemoryPermissionStore` — mutates the shared `InMemorySessionSettings` on `Persist`.
- `Support/StubTool` — bare `ITool` with adjustable `Responder` and an `Invocations` log.
- `Chat/ChatSessionTests` — system prompt placement, `{{workspace}}` substitution, tool-call round-trip via `ChatToolCall.FunctionArguments`.
- `Chat/AgentLoopTests` — plain reply, tool call → final reply, `Ask` approve, `Ask` deny, `Ask` approve-with-project-persistence (verifies the second call within the same turn does NOT raise another approval prompt), settings-level `Deny`, max-iteration cap, unknown tool, malformed JSON, tool throws.

## Phase 4 — Blazor shell + Settings page ✅

`Yamca.Web/Services/`:
- `SessionSettings` — concrete `ISessionSettings`. Holds `Project`/`Global` (`ToolSettingsMap`), `Endpoint`, `SystemPrompt`. Mutators (`SetEndpoint`, `SetSystemPrompt`, `SetToolEntry`) raise `Changed(SettingsTier)`. `HydrateGlobal(json)`/`HydrateProject(json)` deserialize from localStorage payloads; `SerializeGlobal()`/`SerializeProject()` produce the round-trip JSON. **Global blob carries endpoint+systemPrompt+tools; project blob carries only tools** (no per-project endpoint override yet).
- `SettingsTier { Project, Global }` — exposed in the public API alongside `SessionSettings`.
- `SessionSettingsPermissionStore` — `IPermissionStore` adapter; converts `ApprovalPersistence` to `SettingsTier` and calls `SetToolEntry`. The resulting `Changed` event is what drives the localStorage write.
- `LocalStorage` — minimal `IJSRuntime` wrapper (`getItem`/`setItem`/`removeItem`) over `window.yamcaStorage` defined in `wwwroot/js/storage.js`.
- `WorkspaceKey` — derives `yamca.project.<sha256(workspace)>` (lowercased path on Windows/macOS for case-insensitive filesystems). `GlobalKey` is the constant `"yamca.global"`.
- `SettingsHydrator` — bridges the two: `HydrateAsync()` reads both keys and populates session settings (suppresses persistence during apply to avoid the echo); on every `Changed` event it writes the affected tier back. Swallows `JSDisconnectedException`/`TaskCanceledException` so a torn-down circuit doesn't throw.

`Yamca.Web/Program.cs`:
- `IWorkspace` is a singleton bound to `Environment.CurrentDirectory` at process start.
- All five `ITool`s + `IToolRegistry` are singletons.
- Per-circuit (scoped): `SessionSettings` (also registered as `ISessionSettings`), `IPermissionResolver`, `IApprovalCoordinator`, `IPermissionStore`, `LocalStorage`, `WorkspaceKey`, `SettingsHydrator`.
- `AddMudServices()` wired.

`Yamca.Web/Components`:
- `App.razor` loads `_content/MudBlazor/MudBlazor.min.css` + `.min.js` and `js/storage.js` (storage.js **before** blazor.web.js so the JS object exists when interop starts).
- `_Imports.razor` brings in `MudBlazor` and the four `Yamca.Agent.*` namespaces.
- `Layout/MainLayout.razor` — Mud providers (`MudThemeProvider IsDarkMode="true"`, popover, dialog, snackbar) + mini-variant `MudDrawer` with Chat/Settings/About nav. Workspace root path shown in the app bar.
- `Pages/Home.razor` (`/`) — Chat stub pointing at Settings/About (real chat is phase 5).
- `Pages/Settings.razor` (`/settings`) — `@rendermode InteractiveServer`. Hydrates on `OnAfterRenderAsync(firstRender)`. Project/Global tab toggle drives which `ToolSettingsMap` the per-tool dropdowns mutate. Permission column is `MudSelect<PermissionLevel?>` with an explicit `inherit` (null) option; restrict column is `MudSelect<bool?>` shown only when the tool has `SupportsWorkspaceRestriction`. The "Effective" column reflects the live `IPermissionResolver` result, so the merge behavior is visible while editing. **The injected `SessionSettings` is named `Session` here** because the .razor file becomes a class named `Settings` and a member `Settings` collides with it (CS0542).
- `Pages/About.razor` (`/about`) — workspace path, endpoint health-check button (`GET {baseUrl}/models`, sends `Authorization: Bearer` if an API key is set), and the resolved per-tool permission table.

`Yamca.Web/wwwroot/js/storage.js` — try/catch around localStorage so private-mode browsers / disabled-storage don't crash the circuit.

**Gotchas hit during the phase**:
- The compiled class for `Pages/Settings.razor` is named `Settings`; injected `[Inject] SessionSettings Settings` collides → use a different property name (`Session`).
- Inside `MudTable.RowTemplate`, each `MudTd` is a separate child render fragment, so a `var entry = ...` declared in one `@{ }` block is not visible from another `MudTd`. Recompute inside the column that needs it.
- MUD0002 analyzer flagged `PanelClass` on `MudTabs` as illegal in 9.4.0 → dropped it.

## Phase 5 — Chat page + approval UI ✅

`Yamca.Web/Services/`:
- `ChatViewModel` — scoped per circuit. Owns the `AgentLoop` and the `ChatSession`, **both constructed lazily on first `SendAsync`** (so the settings hydrator has already populated `SessionSettings.Endpoint` + `SystemPrompt` before we capture them). `OpenAIChatCompletionClient` is built from `SessionSettings.Endpoint` — `new ChatClient(model, ApiKeyCredential, OpenAIClientOptions { Endpoint = baseUrl })`. If the key is blank we substitute `"sk-local"` so the SDK's credential check doesn't throw against local servers that ignore auth.
  - Maintains `List<ChatTurn> Turns` and `List<PendingApproval> Approvals`, plus `IsRunning`/`Error` flags. Raises `Changed` on every mutation; the Chat page hooks it and calls `InvokeAsync(StateHasChanged)`.
  - Translates `ChatStreamEvent`s into the visible turn structure: `AssistantTokenEvent` appends to the current `AssistantTextItem`; `AssistantMessageEvent` marks it complete; `ToolCallStartedEvent` adds a pending `ToolCallItem`; `ToolCallResultEvent`/`ToolDeniedEvent` updates the matching item by `CallId` (falls back to appending a fresh denied card if no started event preceded — covers the unknown-tool / malformed-JSON paths).
  - `StartApprovalConsumer()` runs a background `Task.Run` reading `IApprovalCoordinator.Pending.ReadAllAsync`. Each request appears as a `PendingApproval` in the `Approvals` list; the page calls `ResolveApproval(...)` which invokes `request.Approve()/Deny()` (resolving the `TaskCompletionSource` the agent loop is awaiting) and drops the prompt from the list. Started in `EnsureStarted()` alongside the loop, cancelled in `Dispose`.
  - `Cancel()` cancels the per-turn CTS. `Clear()` resets the conversation; nulls out `_loop` so the next send creates a fresh `ChatSession` (re-rendering the system prompt against the current settings).
- `ChatTurn` + `ChatTurnItem` hierarchy: `AssistantTextItem` (StringBuilder, IsComplete) and `ToolCallItem` (CallId, ToolName, ArgumentsJson, State, Result). `ToolCallState` enum has `Pending`/`Succeeded`/`Failed`/`Denied`.
- `PendingApproval` wraps an `ApprovalRequest` and exposes pretty-printed args.

`Yamca.Web/Components/`:
- `ApprovalPrompt.razor` — Mud card with arguments preview, `MudRadioGroup<ApprovalPersistence>` (Just once / This project / Everywhere), Allow + Deny buttons. Bubbles up via `OnResolve` callback.
- `ToolCallCard.razor` — collapsible `MudExpansionPanel` with state chip (running/ok/error/denied), pretty-printed args, and result. Indeterminate progress bar while `Pending`.
- `ChatTurnView.razor` — renders the user message bubble, then the assistant's interleaved text + tool-call items. Error alert shown when `Turn.Error` is set (e.g. cancelled).
- `Pages/Home.razor` (`/`) — full chat page. Calls `SettingsHydrator.HydrateAsync()` on first render before allowing input. Uses **Enter to send, Shift+Enter for newline** via `KeyboardEventArgs`. "Cancel" button replaces "Send" while a turn is running. "New chat" button clears (disabled while running).

**Gotchas hit during the phase**:
- The `ChatViewModel` is scoped per circuit, but UI mutations from background tasks (the approval consumer and the agent loop's `await foreach`) happen off the renderer dispatcher. Pattern: view model raises a plain `event Action? Changed`; page handler does `InvokeAsync(StateHasChanged)`. Don't `StateHasChanged()` from inside the view model.
- `Changed` fires from the agent loop's `await foreach` (the calling synchronization context is whichever scheduler picked up the continuation). Subscribers must dispatch.
- Approval requests are matched to tool calls in two stages: the user sees the approval card from the `Pending` channel, **then** later sees the actual `ToolCallStartedEvent` after `RequestApprovalAsync` returns. So the approval card and the started card are separate UI elements, not merged. This avoids needing to pair approvals to specific call IDs (the agent loop doesn't expose the linkage).

## Phase 7 — Optional UI polish

Not in the original plan; pick up only if the manual smoke surfaces a need.

- **Markdown rendering for assistant messages**: currently rendered as `white-space: pre-wrap` plaintext in `ChatTurnView.razor`. Swap in a Blazor-friendly markdown renderer (e.g. `Markdig` + a small component) so code fences, headings, and lists render properly.
- **Auto-scroll to bottom on new content**: `ChatViewModel.Changed` already fires on every token; add a JS interop call (`scrollIntoView` on a sentinel `<div>` at the end of the turn list) after each render. Watch out for not stealing scroll when the user has scrolled up to read history.
- **Show the resolved system prompt**: `ChatSession.SystemPrompt` already exposes the post-substitution string. Surface it on `/about` (or behind a collapsible on `/`) so the user can confirm `{{workspace}}` was replaced as expected before sending a prompt.

## How to verify the current state

From `C:\Repos\yamca`:
```
dotnet build yamca.slnx     # builds all three projects
dotnet test yamca.slnx      # runs NUnit suite
```
Currently expect: **67 passed, 2 skipped**. The 2 skipped are the symlink tests (`Resolve_SymlinkInside*`) which need admin / Developer Mode to create symbolic links.

For phase 4 the additional check is `dotnet build yamca.slnx` (clean) plus `dotnet run --project Yamca.Web`, then loading `/settings` and `/about` in a browser and confirming localStorage entries `yamca.global` and `yamca.project.<sha256>` are written/read on edit and reload.

## Conventions worth knowing

- Tool names use `snake_case` (matches OpenAI function-tool convention). Defined in each tool's `Name` property.
- JSON schemas live inline in each tool as a raw string literal. Keep `additionalProperties: false` so the LLM can't smuggle extra args.
- Sandbox-aware path resolution: always go through `ToolArguments.TryResolvePath` — never call `_workspace.Resolve` directly from a tool.
- Tests: `TempWorkspace` cleans up in `TearDown`. Use `Json.Parse("""{...}""")` for argument literals.
- One `/` separator difference: `ListDirectoryTool` uses `/` to mark directories regardless of OS — readable for the LLM and consistent.
- Workspace paths use `OrdinalIgnoreCase` on Windows and macOS, `Ordinal` on Linux (see `Workspace.PathComparison`).
