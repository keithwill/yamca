---
id: "0002"
title: "Copy Chat Log Message"
branch: 0002-copy-chat-log-message
---
We should add a button next to chat messages which let the user quickly copy the message to the clipboard. This should be an icon button without a label, but with a tooltip stating "Copy to Clipboard". The icon can be a "Copy" icon.

# Implementation Plan

## Investigation Summary

- **Chat messages are rendered in `ChatTurnView.razor`** — this component renders both user messages and assistant messages within a turn.
- **User messages** are a `MudPaper` → `MudText` containing `@Turn.UserMessage`.
- **Assistant messages** are `MudPaper` components containing `AssistantTextItem` (rendered as Markdown or plain text), `ReasoningItem` (via `ReasoningCard`), and `ToolCallItem` (via `ToolCallCard`).
- **Icon button + tooltip pattern** is well-established in `ChatSessionPanel.razor`: a `MudTooltip` wrapping a `<span>` containing a `MudIconButton` with `Variant="Variant.Text"` and `Size="Size.Small"`.
- **Clipboard** requires JS interop — `IJSRuntime` is already injected in `ChatSessionPanel.razor` and used elsewhere.
- **Snackbar feedback** is the standard notification pattern (`ISnackbar.Add`).
- **Icon choice**: `Icons.Material.Filled.ContentCopy` (MudBlazor Material "Content Copy").

## Subtasks

- [ ] **Add `IJSRuntime` and `ISnackbar` to `ChatTurnView.razor`**
  - Add `[Inject]` properties for `IJSRuntime` and `ISnackbar`.
  - Add `using Microsoft.JSInterop;`.

- [ ] **Create a text extraction helper**
  - Add a `private string GetTurnText(ChatTurn turn)` method (or similar) that builds a plain-text representation of a turn:
    - User message: `turn.UserMessage`.
    - For each `AssistantTextItem`: its `Text`.
    - For each `ReasoningItem` (optional): include its `Text` with a clear header like `[Reasoning]`.
    - For each `ToolCallItem` (optional): include tool name, arguments, and result.
  - This keeps the copy logic in one place and avoids duplicating rendering logic.

- [ ] **Add copy button next to user message**
  - Inside the `yamca-user-row` `MudPaper`, add a `MudTooltip` wrapping a `MudIconButton` (positioned to the right of the message).
  - Icon: `Icons.Material.Filled.ContentCopy`, `Size="Size.Small"`, `Variant="Variant.Text"`, `Color="Color.Default"`.
  - Tooltip text: `"Copy to Clipboard"`.
  - On click: call the clipboard copy method (see next subtask).

- [ ] **Add copy button next to each assistant message block**
  - For the `AssistantTextItem` `MudPaper`: add a `MudTooltip` + `MudIconButton` (same styling) at the top-right corner of the paper.
  - For `ReasoningCard` and `ToolCallCard`: consider adding copy buttons inside those components' headers (reuse the same tooltip + icon button pattern).
  - The copy button should be positioned with CSS so it appears unobtrusively (e.g., `ml-auto` or `position: absolute; top: 4px; right: 4px;`).

- [ ] **Implement the clipboard copy logic**
  - Add a `private async Task CopyToClipboardAsync(string text)` method:
    - Use `await JS.InvokeVoidAsync("navigator.clipboard.writeText", text)`.
    - On success: `Snackbar.Add("Copied to clipboard.", Severity.Normal)`.
    - On failure: `Snackbar.Add("Failed to copy to clipboard.", Severity.Error)`.
  - The text to copy should be the plain-text representation from the helper (not HTML).

- [ ] **Style the copy buttons**
  - Ensure copy buttons have appropriate hover states (MudBlazor handles this via `Variant.Text`).
  - Position buttons so they don't interfere with message content — consider using a flex row with `ml-auto` on the button, or an absolute-positioned button in the paper's corner.
  - Add a subtle CSS class (e.g., `.yamca-copy-btn`) if custom positioning is needed.

## Files to Modify

| File | Change |
|------|--------|
| `Yamca.Web/Components/ChatTurnView.razor` | Add `IJSRuntime`, `ISnackbar`, copy buttons, clipboard logic, text extraction helper |

## Design Decisions

1. **Copy the full turn text** — copying a turn produces a single coherent block of text (user message + assistant response), which is more useful than copying individual sub-items.
2. **Plain text, not HTML** — clipboard content is the plain-text representation, not rendered Markdown.
3. **One button per turn** — a single copy button at the top-right of each turn (either user or assistant side) copies the entire turn as plain text. This is simpler than per-item buttons and matches the "copy this message" mental model.
4. **JS interop for clipboard** — `navigator.clipboard.writeText()` is the standard, reliable approach. No external libraries needed.