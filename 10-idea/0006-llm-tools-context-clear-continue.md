---
id: 0006
title: "LLM Tools context_clear_continue"
---

We should provide a tool to the LLM that lets it restart the chat with a fresh context and a new prompt. This would be provided for the LLM in cases where it has completed steps on a board item and has been instructed to check its context size before continuing work on the board item. In those cases it could structure a prompt to continue the unfinished work. In effect it would be similar to a compact and continue, but initiated by the LLM at a natural checkpoint instead of arbitrarily after hitting an unrelated limit in between completed steps on a board.
