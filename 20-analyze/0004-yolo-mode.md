---
id: 0004
title: "Yolo Mode"
branch: 0004-yolo-mode
---

We should add a toggle button to the chat prompt area which turns on Yolo mode. Yolo mode elides all permission checks for the current chat session while it is turned on. They are all considered as implicitly accepted by the user. Other systems usually call this "skipping permissions". This is a mode that is inherently insecure but is provided to reduce user frustration in scenarios where they are asking the LLM to perform many command executions in a row, and don't want to modify their default permission settings to get around the frustration.
