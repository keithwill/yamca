# Commands

Registered commands are one-line CLI commands you hint to the LLM ahead of time,
managed at `/commands`. They give the model a curated set of known entry points —
things like `npm install` or `dotnet build` — that it can run by name instead of
guessing the exact invocation.

## Registering a command

Each entry is a single CLI command line. The LLM runs it verbatim by passing the
exact command line — or the command's optional **name** — as the script path; no
arguments are appended. Add the commands you'd like the LLM to reach for, and
optionally give each one:

- A **name** — a short, stable handle the LLM can reference instead of
  reproducing the exact command line, which avoids brittle whitespace/quoting
  mismatches. When set, it is also the default process name when the command is
  launched in the background; with no name, the command line itself becomes the
  process name.
- A **description** — shown to the LLM at session start so it can pick the right
  command for a task.
- The **Background** flag — for a watcher or dev server. Running a
  background-flagged command launches a long-lived process (managed on the
  Processes page, and via `get_process_output` / `list_processes` /
  `stop_process`) instead of running it to completion.
- A **Hide Success** toggle — on a successful (exit 0) run, return only the
  status to the LLM and withhold stdout/stderr to save context; failures still
  return full output. It doesn't apply to a background command and is cleared
  when Background is set.

## Project vs. User tiers

Commands come in two tiers, switchable on the page:

- **Project** — stored in the project settings file, specific to this workspace.
- **User** — stored in the user settings file, available everywhere.

## See also

- [scripts.md](scripts.md) — registering script files and directories
- [tools-and-permissions.md](tools-and-permissions.md) — per-tool permissions and background-process tools
