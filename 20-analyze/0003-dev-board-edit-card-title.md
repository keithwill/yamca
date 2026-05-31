---
id: 0003
title: "Dev Board Edit Card Title"
branch: 0003-dev-board-edit-card-title
---

## Analysis

Currently, card titles are read from the `title:` frontmatter field (or fall back to the first `# heading` or filename stem). There is no dedicated tool or helper to edit just the title — `board_update_card` replaces the entire file content, which is cumbersome for a simple title change.

### Existing pattern to follow

`BoardService` already has helper methods that modify frontmatter fields without touching the body:
- `WithBranch(rawText, branch)` — sets `branch:`
- `WithCommit(rawText, sha)` — sets `commit:`
- `WithPriority(rawText, priority)` — sets `priority:`
- `WithBody(rawText, body)` — replaces the body, preserves frontmatter

All of these delegate to the private `WithFrontmatterField(rawText, key, value)` which handles adding/replacing a scalar frontmatter key.

### Implementation plan

1. **Add `WithTitle` static method to `BoardService`** — follows the exact same pattern as `WithBranch`/`WithCommit`/`WithPriority`: a thin wrapper around `WithFrontmatterField` that sets the `title:` key.

2. **Create `BoardEditCardTitleTool`** — a new `ITool` that:
   - Takes `card` (id or filename) and `title` (new title string) parameters
   - Reads the card file via `BoardWorktree.MutateAsync`
   - Applies `BoardService.WithTitle(rawText, newTitle)`
   - Writes back and commits to the board branch
   - Default permission: `Ask` (mutation tool)
   - `SupportsWorkspaceRestriction => false` (board tools are never workspace-restricted)

3. **Register the tool** — add `BoardEditCardTitleTool` to the DI container in `Program.cs` alongside the other board tools.

4. **Add unit tests** — tests for `WithTitle` in `BoardServiceTests` and tool tests in `BoardToolsTests`.

## Subtasks

- [ ] Add `BoardService.WithTitle(string rawText, string title)` static method
- [ ] Create `BoardEditCardTitleTool` class in `Yamca.Agent/Tools/Board/`
- [ ] Register `BoardEditCardTitleTool` in DI (`Program.cs`)
- [ ] Add unit tests for `WithTitle` in `BoardServiceTests`
- [ ] Add unit tests for `BoardEditCardTitleTool` in `BoardToolsTests`
- [ ] Build and run tests
