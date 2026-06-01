---
id: 0004
title: "Yolo Mode"
branch: 0004-yolo-mode
commit: 8404b40f0224fc909b6d13003d5260323b52526c
---

We should add a toggle button to the chat prompt area which turns on Yolo mode. Yolo mode elides all permission checks for the current chat session while it is turned on. They are all considered as implicitly accepted by the user. Other systems usually call this "skipping permissions". This is a mode that is inherently insecure but is provided to reduce user frustration in scenarios where they are asking the LLM to perform many command executions in a row, and don't want to modify their default permission settings to get around the frustration.

## Analysis

### How permissions currently work

The permission gating flow is:

1. **`AgentLoop.HandleToolCallAsync`** (line 163 of `AgentLoop.cs`) calls `_permissions.Resolve(call.ToolName)` to get the `PermissionLevel`.
2. If the result is `PermissionLevel.Ask`, it calls `_approvals.RequestApprovalAsync(...)` which pushes an `ApprovalRequest` into a channel.
3. **`ChatViewModel.StartApprovalConsumer`** (line 502) reads from that channel and adds `PendingApproval` objects to `ChatViewModel.Approvals`.
4. **`ChatSessionPanel.razor`** (line 200-205) renders each `PendingApproval` as an `ApprovalPrompt` card with Allow/Deny buttons.
5. Clicking Allow/Deny calls `ChatViewModel.ResolveApproval(...)`, which resolves the underlying `ApprovalRequest`.

The permission resolution itself (`IPermissionResolver.Resolve`) reads from `ISessionSettings` (project → global → tool default). This is **not** the right place to inject Yolo mode, because Yolo is per-session, not global/project-level.

### Key design decisions

- **Yolo mode is per-chat-session, not global.** It's a transient toggle that lives in `ChatViewModel`, not in `ISessionSettings` or `SessionSettings`. It resets when the chat is cleared.
- **The toggle is a UI-only concern at the ViewModel level.** `AgentLoop` needs to know whether Yolo is active so it can skip the approval request. The cleanest approach is a `bool YoloMode` property on `AgentLoop` itself.
- **When Yolo is on, `_permissions.Resolve()` is still called** (the tool's permission level is still determined), but if the level would be `Ask`, instead of queuing an approval prompt, the tool is immediately approved.
- **Re-reading the card:** *"Yolo mode elides all permission checks for the current chat session while it is turned on. They are all considered as implicitly accepted by the user."* — this means **all** checks, including explicit Deny. So Yolo makes everything effectively `PermissionLevel.Allow`.

### Where to inject

The injection point is in `AgentLoop.HandleToolCallAsync` at lines 163-174:

```csharp
var level = _permissions.Resolve(call.ToolName);
if (level == PermissionLevel.Ask)
{
    var decision = await _approvals.RequestApprovalAsync(...);
    ...
}
```

With Yolo mode, we want to treat every tool as `PermissionLevel.Allow` — skip the resolution entirely and jump straight to execution.

### UI location

The composer toolbar in `ChatSessionPanel.razor` has a `yamca-composer-left` div with buttons for attach, endpoint picker, branch, compact, merge, delete. This is the natural place for a Yolo toggle button. It should appear as a `MudIconButton` with a distinctive icon and a tooltip.

## Implementation Plan

### 1. Add `YoloMode` property to `AgentLoop`
**File:** `Yamca.Agent/Chat/AgentLoop.cs`

- Add a `public bool YoloMode { get; set; }` property to `AgentLoop`.
- In `HandleToolCallAsync`, after the tool-exists / availability / JSON-parse checks
  (which should still run), skip the permission resolution and approval flow when
  `YoloMode` is true:
  - If `YoloMode`, treat the effective permission as `PermissionLevel.Allow`
    (skip `_permissions.Resolve()` and `_approvals.RequestApprovalAsync()` entirely).
  - The tool still gets `ToolCallStartedEvent` → execute → `ToolCallResultEvent`.
- This keeps the change localized to the agent loop — no changes to `IPermissionResolver`,
  `IApprovalCoordinator`, or `ISessionSettings`.

### 2. Wire `YoloMode` through `ChatViewModel`
**File:** `Yamca.Web/Services/ChatViewModel.cs`

- Add a `public bool YoloMode { get; set; }` property to `ChatViewModel`.
- In `EnsureStarted()`, after constructing the `AgentLoop`, set `_loop.YoloMode = YoloMode`.
- Subscribe to `YoloMode` changes and update the loop: add a `RaiseYoloMode()` method
  that sets `_loop?.YoloMode = YoloMode` and calls `Raise()`.
- In `Clear()`, reset `YoloMode = false` (Yolo is session-scoped).

### 3. Add Yolo toggle button to the composer toolbar
**File:** `Yamca.Web/Components/Chat/ChatSessionPanel.razor`

- Add a `MudIconButton` in the `yamca-composer-left` div (after the compact button,
  before the worktree buttons).
- The button toggles `Chat.YoloMode` via `OnClick`.
- Use a distinctive icon: `Icons.Material.Filled.ShieldOff` (Yolo = no shields).
- Show a `MudTooltip` with text like "Yolo Mode: ON" / "Yolo Mode: OFF".
- Change button color when active (e.g., `Color="Color.Error"` when ON, `Color="Color.Default"` when OFF).
- The button is disabled when `Chat.IsReadOnly`.

### 4. Add visual feedback / status indicator
**File:** `Yamca.Web/Components/Chat/ChatSessionPanel.razor`

- When Yolo mode is active, show a subtle visual indicator near the composer or at the
  top of the chat scroll area (e.g., a small yellow/red chip/banner saying "YOLO").
- This is important because Yolo is "inherently insecure" — the user should always see
  whether it's active.
- Consider a thin colored top-border on the chat scroll area or a small banner inside
  the scroll container that disappears when scrolling up.

### 5. Write unit tests for Yolo mode in AgentLoop
**File:** `Yamca.Agent.Tests/Chat/AgentLoopTests.cs`

- `YoloMode_AllowsAskTool_WithoutApprovalPrompt` — set up an Ask-level tool, enable YoloMode, verify the tool executes without any approval request appearing on the coordinator.
- `YoloMode_AllowsDeniedTool_FromSettings` — set up a tool explicitly denied in settings, enable YoloMode, verify the tool executes (permission check is fully elided).
- `YoloMode_Off_Standard_Ask_Flow` — verify that without Yolo, the standard Ask → approval → approve/deny flow still works.

### 6. Add YoloMode to chat persistence — skip (v1 decision)
**Decision:** Do NOT persist YoloMode to disk. Yolo is inherently a transient, per-session
safety override. If a chat is reloaded from history, Yolo should be OFF. This is the
correct default — you don't want Yolo to silently persist across sessions.

### 7. Update planning.md
**File:** `planning.md`

- Add Yolo Mode to the Short-list section with a brief description.
- Optionally add a note about the security implications.

## Files to modify (summary)

| File | Change |
|------|--------|
| `Yamca.Agent/Chat/AgentLoop.cs` | Add `YoloMode` property; skip permission check when true |
| `Yamca.Web/Services/ChatViewModel.cs` | Add `YoloMode` property; wire to loop; reset on Clear |
| `Yamca.Web/Components/Chat/ChatSessionPanel.razor` | Add toggle button + visual indicator |
| `Yamca.Agent.Tests/Chat/AgentLoopTests.cs` | Add 3 unit tests |
| `planning.md` | Document feature |

## Risk assessment

- **Low risk:** The change is localized. `AgentLoop.HandleToolCallAsync` has a single
  well-defined injection point. The UI change is a single button.
- **No API changes:** No new interfaces, no new DI registrations, no new settings.
- **No persistence concerns:** YoloMode is not persisted — it resets on chat clear / reload.
- **Testing is straightforward:** The existing `AgentLoopTests` pattern (stub tools,
  fake LLM, in-memory settings) maps directly to Yolo tests.
