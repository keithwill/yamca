# Commands

Registered commands are one-line CLI commands you hint to the LLM ahead of time,
managed at `/commands`. They give the model a curated set of known entry points —
things like `npm install` or `dotnet build` — that it can run by name instead of
guessing the exact invocation.

## Registering a command

Each entry is a single CLI command line. Add the commands you'd like the LLM to
reach for, and optionally give each one:

- A **name** — a short, stable handle the LLM can reference instead of
  reproducing the exact command line, which avoids brittle whitespace/quoting
  mismatches. The name is also the default process name when the command is
  launched in the background (see below).
- The **Background** flag — for a watcher or dev server. Running a
  background-flagged command launches a long-lived process (managed on the
  Processes page) instead of running it to completion.

## Project vs. User tiers

Commands come in two tiers, switchable on the page:

- **Project** — stored in the project settings file, specific to this workspace.
- **User** — stored in the user settings file, available everywhere.

## See also

- [scripts.md](scripts.md) — registering script files, directories, and inline commands
- [tools-and-permissions.md](tools-and-permissions.md) — per-tool permissions and background-process tools
