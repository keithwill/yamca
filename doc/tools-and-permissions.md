# Tools and Permissions

Tools are the actions the agent can take in your workspace — reading and writing
files, searching, running commands, and navigating code. Every tool call runs
through a **permission** check first. Configure both at `/tools`.

![The tools and permissions page](img/tools.png)

## Built-in tools

| Category | Tools |
|----------|-------|
| **Files** | `read_file`, `write_file`, `edit_file`, `delete_file`, `list_directory` |
| **Search** | `grep`, `find_files` |
| **Execution** | `execute_command`, `execute_script`, `execute_registered_script`, `execute_discovered_script` |
| **Background processes** | `start_process`, `get_process_output`, `stop_process`, `list_processes` |
| **Git** | `git` (the LLM-facing tool); `git_read`, `git_write` (its permission identities) |
| **Code intelligence** | `code_search`, `code_list_symbols`, `code_find_definitions`, `code_find_calls`, `code_find_references`, `code_extract_symbol`, `code_edit_symbol`, `code_surrounding_context` |
| **Dev board** | `board_list`, `board_get_card`, `board_get_step_instructions`, `board_move_card`, `board_update_card`, `board_reinit` |
| **Subagents** | `subagent_run`, `loop` |
| **Tool discovery** | `lookup_tool`, `call_tool` |

The code-intelligence tools understand symbols across ~12 languages (C#, C, C++,
Java, JavaScript, TypeScript/TSX, Python, Ruby, PHP, Rust, Go, …) so the agent
can extract or edit a function/class by name instead of by line range.

[MCP servers](mcp.md) contribute additional tools beyond this built-in set.

### The `git` tool

The agent runs git through a single `git` tool that accepts an `operation` (a
curated subcommand) and `arguments` passed verbatim as argv. Because it spawns
`git` directly with no shell, shell metacharacters in the arguments (`;`, `&&`,
`|`, `$()`) are inert — unlike a raw `execute_command`, there is no command-line
to inject into. The model only sees this one tool; it never appears as dozens of
per-subcommand entries.

Permissions are split into two identities so reads and writes can be governed
separately:

- **`git_read`** — non-mutating subcommands (`status`, `log`, `diff`, `show`,
  `blame`). Defaults to **Allow**: these cannot change the repository no matter
  what arguments are supplied.
- **`git_write`** — mutating subcommands (`add`, `restore`, `commit`, `switch`,
  `branch`, `stash`, `fetch`, `pull`, `push`). Defaults to **Ask**.

The curated list is intentionally small — the common day-to-day subcommands. For
anything outside it (e.g. `rebase`, `reset`, `cherry-pick`), the agent falls back
to `execute_command`, or you run it yourself outside yamca.

### Background-process tools

`execute_command` runs a command to completion. The background-process tools instead
start a **long-lived** process — a dev server, watcher, or worker — and leave it running
while the chat continues:

- **`start_process`** (default **Ask**) — launches a process under the session's configured
  shell and returns immediately. The caller gives it a stable `name` (e.g. `"web"`) used by
  the other tools, and may supply a `working_directory`, an optional `stop_command`, and the
  `ports` it listens on. Starting a `name` that is already running **reuses** the existing
  process rather than spawning a duplicate (dedupe-by-name).
- **`get_process_output`** (default **Allow**) — reads the buffered stdout/stderr. Pass the
  `next_cursor` from a previous call as `since` to fetch only new output.
- **`stop_process`** (default **Ask**) — runs the `stop_command` if set, waits a grace
  period, then force-kills the process tree.
- **`list_processes`** (default **Allow**) — lists every process with pid, status, ports,
  and uptime.

Processes are **OS-wide** and owned by one process-wide manager: a process started in one
chat session keeps running after that session ends and stays visible to other sessions and
to the **Processes** sidebar page (where it can be viewed, restarted, or stopped). All
running processes are stopped gracefully when Yamca shuts down. These tools are
[deferred](#availability) by default, so their schemas never enter the prompt prefix.

## Permission levels

Each tool resolves to one of three levels (`PermissionLevel`):

- **Allow** — runs without prompting.
- **Ask** — pauses for your approval before each call.
- **Deny** — the tool is refused.

Resolution is layered: the **Project** setting wins if set, otherwise the
**User** setting applies. The User tier always carries an explicit value for
every tool — on load it's seeded from each tool's built-in `DefaultPermission`
(see [Default philosophy](#default-philosophy)) — so there's no "inherit" to
fall through at the User level. A Project setting left as *inherit* falls
through to User. So you can set a standing preference per tool across all
workspaces, then override it per project.

### Workspace restriction

File-touching tools can be restricted to the workspace sandbox. When
`RestrictToWorkspace` is on, paths are clamped to the session's `RootPath` and an
attempt to escape it is refused (`PathOutsideWorkspaceException`). This resolves
with the same Project → User → tool-default precedence.

### Default philosophy

The shipped defaults are tuned for the primary audience: developers modifying
code in a git repository, typically on a throwaway worktree branch that
segregates the agent's work. Under that assumption, the whole-file tools
`write_file` and `delete_file` default to **Allow** — but only *within the
workspace*, because workspace restriction is on by default for every
file-touching tool. The in-place edit tools (`edit_file` and `code_edit_symbol`)
default to **Ask** so you see a diff before each surgical change lands, as does
running an arbitrary shell command (`execute_command`). For everything that does
default to Allow, the safety net is the workspace boundary plus version control,
not a prompt on every write.

## Availability

Separately from permissions, **availability** controls what the LLM even *sees*:

- **Eager** — the tool's schema is in every tool list.
- **Deferred** — the tool is discovered on demand via `lookup_tool` and invoked
  via `call_tool`. Its schema stays out of the prompt prefix, which preserves
  prompt-cache hits. MCP tools are always deferred.
- **Hidden** — invisible to the model.

Like permissions, availability resolves Project over User. The User tier is
seeded with each tool's default availability, so a Project value left as
*inherit* falls through to User.

## Approval flow

When a tool resolves to **Ask**, the agent raises an approval request that
surfaces in the chat UI. You approve or reject the specific call; for edit tools
the request includes a diff so you can see the change before it lands.

## See also

- [scripts.md](scripts.md) — registered vs. discovered scripts and their distinct permissions
- [mcp.md](mcp.md) — tools contributed by MCP servers
- [settings-and-backup.md](settings-and-backup.md) — Project vs. User settings tiers
