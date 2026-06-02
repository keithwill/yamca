# Tools and Permissions

Tools are the actions the agent can take in your workspace — reading and writing
files, searching, running commands, and navigating code. Every tool call runs
through a **permission** check first. Configure both at `/tools`.

## Built-in tools

| Category | Tools |
|----------|-------|
| **Files** | `read_file`, `write_file`, `edit_file`, `delete_file`, `list_directory` |
| **Search** | `grep`, `find_files` |
| **Execution** | `execute_command`, `execute_script`, `execute_registered_script`, `execute_discovered_script` |
| **Code intelligence** | `code_search`, `code_list_symbols`, `code_find_definitions`, `code_find_calls`, `code_find_references`, `code_extract_symbol`, `code_edit_symbol`, `code_surrounding_context` |
| **Dev board** | `board_list`, `board_get_card`, `board_get_step_instructions`, `board_move_card`, `board_update_card`, `board_reinit` |
| **Tool discovery** | `lookup_tool`, `call_tool` |

The code-intelligence tools understand symbols across ~12 languages (C#, C, C++,
Java, JavaScript, TypeScript/TSX, Python, Ruby, PHP, Rust, Go, …) so the agent
can extract or edit a function/class by name instead of by line range.

[MCP servers](mcp.md) contribute additional tools beyond this built-in set.

## Permission levels

Each tool resolves to one of three levels (`PermissionLevel`):

- **Allow** — runs without prompting.
- **Ask** — pauses for your approval before each call (the default when nothing
  else is set).
- **Deny** — the tool is refused.

Resolution is layered: **Project** setting wins, else **User**, else the
tool's built-in `DefaultPermission`. So you can loosen or tighten a tool across
all workspaces and still override it per project.

### Workspace restriction

File-touching tools can be restricted to the workspace sandbox. When
`RestrictToWorkspace` is on, paths are clamped to the session's `RootPath` and an
attempt to escape it is refused (`PathOutsideWorkspaceException`). This resolves
with the same Project → User → tool-default precedence.

## Availability

Separately from permissions, **availability** controls what the LLM even *sees*:

- **Eager** — the tool's schema is in every tool list.
- **Deferred** — the tool is discovered on demand via `lookup_tool` and invoked
  via `call_tool`. Its schema stays out of the prompt prefix, which preserves
  prompt-cache hits. MCP tools are always deferred.
- **Hidden** — invisible to the model.

Like permissions, availability is set per **Project** or **User** tier, with
Project overriding User, and unset (*inherit*) falling through to the tool's
default.

## Approval flow

When a tool resolves to **Ask**, the agent raises an approval request that
surfaces in the chat UI. You approve or reject the specific call; for edit tools
the request includes a diff so you can see the change before it lands.

## See also

- [scripts.md](scripts.md) — registered vs. discovered scripts and their distinct permissions
- [mcp.md](mcp.md) — tools contributed by MCP servers
- [settings-and-backup.md](settings-and-backup.md) — Project vs. User settings tiers
