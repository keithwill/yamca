# Planning

Feature decisions for Yamca, so we don't keep re-litigating the same ideas.

- **Short-list** — things we want and intend to build (not yet scheduled or designed).
- **Out of scope** — deliberately ruled out.
- **Tentatively out of scope** — ruled out for now, but reasonably likely to be
  reopened if circumstances change; the burden is on a new proposal to address
  the rationale.

_Last reviewed: 2026-05-31._

---

## Short-list

### Image / vision attachments
Attach or paste images (PNG, screenshots) to send to multimodal models. The
composer already has a paperclip button, but attachments are currently read as
text only. **High confidence — we'll almost certainly do this.**

This could go well with browser screen capture apis. We should investigate this interaction.

### Copy button
One-click copy on assistant messages and code blocks. **Definitely wanted.**

### Conversation search
Search text across saved chat history (chats are already persisted to disk).
Possibly back this with a background indexer of some kind.

### Manual context-window override
Allow the user to set a manual max-context value next to model selection in the
endpoint configuration, for when the backend doesn't report one.

### "Explore Directory" in chat history modal
Add a button to the chat history modal that opens the on-disk chat files folder,
so users can see/use the persisted chat files directly. (Chosen alternative to a
dedicated per-conversation exporter.)

### Drag-and-drop file attach
Support dragging and dropping a file onto the chat prompt to attach it, in
addition to the existing attach button.

---

## Out of scope

### Message edit / regenerate / retry
Editing a sent prompt and resending, regenerating a response, or retrying after
an error. (May be reconsidered later.)

**Why:** Retries that the LLM did not itself ask for can cause it to hallucinate
badly. The value isn't worth that failure mode today.

### Per-conversation export
Exporting a single chat to markdown/JSON (distinct from the global Backup).

**Why:** Chat sessions are already persisted as files on disk under `.yamca/chat`,
so they are usable as backups as-is. Rather than a bespoke exporter, we'd prefer
an "Explore Directory" affordance in the chat history modal so users can open the
files directly (see short-list).

### Light / system theme toggle
Dark mode is hardcoded; no light/system toggle.

**Why:** Not a direction we want to invest in.

### `@file` mention in composer
Referencing a workspace file inline by path from the composer.

**Why:** We already have a file attachment button. (Drag-and-drop file attach is
on the short-list instead.)

---

## Tentatively out of scope

### Sampling parameters (temperature, max_tokens, top_p, stop)
Per-endpoint sampling/inference parameters in `EndpointSettings`.

**Why:** This would ensnare Yamca in endless, constantly-changing inferencing
concerns. Better to rely on the endpoint being configured as the user desires —
someone running llama-server is already tuning sampling parameters there, not in
their agent harness.

### Diff / review view
Showing the git diff of an agent's worktree changes before merging.

**Why:** Presenting a diff usefully would require syntax highlighting and extra
git interactions that complicate the system, when most users will just view the
changes in their IDE. Yamca is a tool for devs who already have code-editing
tools and use them alongside Yamca.

### In-UI commit affordance
Staging/committing the agent's work from within Yamca.

**Why:** We rely on the user prompting the agent to commit, or on their IDE.
