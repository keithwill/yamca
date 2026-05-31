---
id: 0001
title: "Fix New Card Layout"
branch: 0001-fix-new-card-layout
commit: 602c5b06469dd5098795309cfed08399f2baf48e
---

In the dev board we have two dialogs for cards. One for creating a card and one for editing/view an existing card (card details dialog). The card details dialog has had its design updated, but the new card dialog was not updated and has deviated. For example, it does not use the chip dropdown component for binding card priority.

Update the new card modal layout to be consistent with the card details dialog.

## Analysis

Two files involved:

- **`Yamca.Web/Components/Board/CardDetailDialog.razor`** — the reference design (updated)
- **`Yamca.Web/Components/Board/NewCardDialog.razor`** — the dialog to update

Shared component: **`Yamca.Web/Components/ChipDropdown.razor`** — a `<MudMenu>`-backed chip that opens a dropdown of `<MudMenuItem>` options. Used in `CardDetailDialog` for both priority and endpoint selection.

### Specific differences to fix

1. **Priority selector**: `NewCardDialog` uses a `<MudSelect>` dropdown in a labelled row (`<MudText>` + `<MudSelect>`). Should use `<ChipDropdown>` with `<MudMenuItem>` children, matching the `CardDetailDialog` pattern (flag icon, color-coded for High priority, same menu item labels "↑ High" / "Normal" / "↓ Low").
2. **Layout**: The priority chip should sit inline (e.g., in a flex row below the title field) rather than occupying its own labelled form row.

### What stays the same

- Title text field, body text field, Cancel/Create buttons — all remain as-is.
- The `Result` record and the `AddCardAsync` caller in `Board.razor` need no changes (same `CardPriority` enum flows through).

## Implementation plan

- [x] Replace the priority `<MudSelect>` row in `NewCardDialog.razor` with a `<ChipDropdown>` matching the `CardDetailDialog` pattern (flag icon, color/style binding, three menu items)
- [x] Place the chip in a compact flex row below the title field (consistent visual spacing)
- [x] Add `@using Yamca.Web.Components` if needed (already covered by `_Imports.razor`)
- [x] Verify the dialog renders correctly and priority selection still produces the correct `CardPriority` value in `Result`
