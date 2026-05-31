---
id: 0003
title: "Deleting Dev Card Cleanup"
---

When deleting a dev board card we should check if the card is bound to a branch. If it is, we should prompt the user if they'd like to remove the worktree and delete the branch. The prompt should indicate if the branch has unmerged changes.
