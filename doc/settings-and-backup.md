# Settings and Backup

yamca's configuration is split into two tiers and two pages let you tune behavior
(`/preferences`) and move your user configuration between machines
(`/backup`).

## Project vs. User tiers

Most settings — tool permissions, instruction files, registered scripts — exist
at two levels:

- **Project** — stored on disk per workspace in the project settings file
  (`.yamca/...`). Specific to that repository and travels with it.
- **User** — stored in the user settings file in your user config directory.
  Applies across every workspace.

Where both can apply, **Project overrides User**, and an unset/*inherit* value
falls through to User and then to the built-in default. Endpoints and MCP
servers are user-only.

## Preferences

`/preferences` collects per-user chat behavior:

- **Markdown rendering** — toggles whether replies render as Markdown. A
  capability hint is appended to the system prompt either way: on tells the model
  its output is rendered as Markdown; off tells it to reply in plain text.
- **Reasoning blocks** — how to display chain-of-thought from reasoning models
  (Hidden / Collapsed / Expanded). See [chat-sessions.md](chat-sessions.md).
- **Auto-compaction** — folds older turns into a summary as context grows so long
  sessions keep working.

## Backup (export / import)

`/backup` moves your **User** settings between machines:

- **Export** writes your user settings to a JSON file.
- **Import** restores them from such a file.

Project settings are **not** included — they live per workspace on disk and
travel with the repository instead.

> **Note:** exported user settings include your endpoints, and therefore any
> **API keys** stored on them. Treat an export file as a secret.

## See also

- [tools-and-permissions.md](tools-and-permissions.md) — Project/User precedence for tools
- [endpoints.md](endpoints.md) — user-only endpoint configuration
- [chat-sessions.md](chat-sessions.md) — what the chat preferences affect
