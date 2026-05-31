---
id: 0001
title: "Fix New Card Layout"
branch: 0001-fix-new-card-layout
---

In the dev board we have two dialogs for cards. One for creating a card and one for editing/view an existing card (card details dialog). The card details dialog has had its design updated, but the new card dialog was not updated and has deviated. For example, it does not use the chip dropdown component for binding card priority.

Update the new card modal layout to be consistent with the card details dialog.
