---
id: 0005
title: "LLM Tools context_stats"
---

We should provide a tool to the LLM for checking the current context stats. This would provide the current context size, the maximum size, and any other details that would be relevant for the LLM to make actionable decisions about its context. The maximum size should be based on the auto compaction limit (preferred), or the model's maximum context size.

We will have a separate feature in the future providing tools letting the LLM clear its context and continue with a subsequent prompt. This should be useful in cases where we want to let the LLM decide if clearing its context between steps on a card would be useful (and it could take into account how close it is to a full useful context to make that decision).
