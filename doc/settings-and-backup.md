# Settings and Backup

yamca's configuration is split into two tiers and two pages let you tune behavior
(`/preferences`) and move your user configuration between machines
(`/backup`).

## Project vs. User tiers

Most settings — tool permissions, instruction files, registered scripts — exist
at two levels:

- **Project** — stored on disk per workspace in the project settings file
  (`.yamca/project.json`). Specific to that repository and travels with it.
- **User** — stored in the user settings file in your user config directory.
  Applies across every workspace.

Where both can apply, **Project overrides User**, and a Project value left as
unset/*inherit* falls through to User. For tool permissions the User tier is
seeded with each tool's built-in default on load, so it always holds an explicit
value (no inherit at the User level — see
[tools-and-permissions.md](tools-and-permissions.md)). Endpoints and MCP servers
are user-only.

## Preferences

`/preferences` collects per-user chat behavior:

- **Markdown rendering** — toggles whether replies render as Markdown. A
  capability hint is appended to the system prompt either way: on tells the model
  its output is rendered as Markdown; off tells it to reply in plain text.
- **Reasoning blocks** — how to display chain-of-thought from reasoning models
  (Hidden / Collapsed / Expanded). See [chat-sessions.md](chat-sessions.md).
- **Auto-compaction** — folds older turns into a summary as context grows so long
  sessions keep working. A trigger threshold (% of the context window) and the
  number of recent turns to keep verbatim are configurable.
- **Max tool call iterations** — caps how many LLM round-trips a single turn may
  make before the agent stops.
- **Command shell** — which shell the `execute_command` tool and registered
  inline scripts run through. Only shells detected on this machine are listed;
  auto-detect picks pwsh → Windows PowerShell → cmd.exe on Windows, and bash → sh
  elsewhere.

## Backup (export / import)

`/backup` moves your **User** settings between machines:

- **Export** writes your user settings to a JSON file.
- **Import** restores them from such a file.

Project settings are **not** included — they live per workspace on disk and
travel with the repository instead.

> **Note:** exported user settings include your endpoints. The export dialog has
> an opt-in **Include API key** checkbox (off by default) — leave it unchecked and
> keys are stripped from the file; check it and the file holds your **API keys** in
> plaintext, so treat such an export as a secret.

## See also

- [tools-and-permissions.md](tools-and-permissions.md) — Project/User precedence for tools
- [endpoints.md](endpoints.md) — user-only endpoint configuration
- [chat-sessions.md](chat-sessions.md) — what the chat preferences affect
