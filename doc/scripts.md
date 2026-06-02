# Scripts

yamca distinguishes between scripts you **register** ahead of time and scripts the
agent **discovers** on its own, giving each a different trust level. Manage
registered scripts at `/scripts`.

## Why the distinction

A registered script is one you've explicitly told yamca about, so it can run
under different (typically looser) tool permissions than an arbitrary script the
LLM happens to find through file operations. This lets you green-light your known
build/test/lint entry points without granting blanket execution rights.

The relevant tools (see [tools-and-permissions.md](tools-and-permissions.md)):

- **`execute_registered_script`** — runs a known, registered script.
- **`execute_discovered_script`** — runs a script the LLM found by other means.
- **`execute_script`** / **`execute_command`** — general execution.

Because each is a separate tool, you can set their permissions independently —
e.g. allow registered scripts while leaving discovered-script execution on *Ask*.

## Registering a script

Each registered entry is a **workspace-relative** path:

- A file path registers that single script.
- A directory path registers everything inside it — files within a registered
  directory count as registered.
- An optional **description** is shown to the LLM at session start so it can pick
  the right entry for a task.

## Project vs. User tiers

Registered scripts come in two tiers, switchable on the page:

- **Project** — stored in the project settings file, specific to this workspace.
- **User** — stored in the user settings file, available everywhere.

## Execution

Scripts run through a resolver that picks the right interpreter for the file
(`InterpreterResolver` / `ScriptRunner`), so you register the script path rather
than a full interpreter command line.

## See also

- [tools-and-permissions.md](tools-and-permissions.md) — per-tool permissions for the execute tools
- [settings-and-backup.md](settings-and-backup.md) — Project vs. User settings tiers
