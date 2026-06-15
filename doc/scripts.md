# Scripts

yamca distinguishes between scripts you **register** ahead of time and scripts the
agent **discovers** on its own, giving each a different trust level. Manage
registered scripts at `/scripts`.

![The registered scripts page](img/scripts.png)

## Why the distinction

A registered script is one you've explicitly told yamca about, so it runs through
the always-allowed execution path instead of prompting. This lets you green-light
your known build/test/lint entry points without granting blanket execution rights.

The relevant tools (see [tools-and-permissions.md](tools-and-permissions.md)):

- **`execute_allowed`** — runs a pre-allowed entry: a registered command by name, or
  a registered script by its workspace-relative path (including any file under a
  registered directory). Its permission is fixed at *Allow* — being the allowlist is
  its whole purpose — so it isn't user-configurable.
- **`execute_script`** — runs an **unregistered** script by workspace-relative path.
  Defaults to *Ask*, and the approval prompt offers to add the script to the
  registry; once registered it runs via `execute_allowed` instead. A path that is
  already registered is refused and redirected to `execute_allowed`.
- **`execute_command`** — a separate tool for running an arbitrary shell command
  line (not a script file). Defaults to *Ask*.

So you can let your registered scripts run freely while leaving ad-hoc script
execution on *Ask*.

## Registering a script

The `/scripts` page registers two kinds of entry:

- **File** — a workspace-relative path that registers that single script.
- **Directory** — a workspace-relative path that registers everything inside it;
  files within a registered directory count as registered.

Each entry also takes:

- An optional **description**, shown to the LLM at session start so it can pick
  the right entry for a task.
- A **Hide Success** toggle: on a successful (exit 0) run, return only the status
  to the LLM and withhold stdout/stderr to save context. Failures still return
  full output.

One-line CLI commands with no backing file in the repo (e.g. `npm install`,
`dotnet build`) are a separate kind — **inline commands** — registered on the
`/commands` page rather than here, including their background and *Hide Success*
behavior. See [commands.md](commands.md).

## Project vs. User tiers

Registered scripts come in two tiers, switchable on the page:

- **Project** — stored in the project settings file, specific to this workspace.
- **User** — stored in the user settings file, available everywhere.

## Execution

Scripts run through a resolver that picks the right interpreter for the file
(`InterpreterResolver` / `ScriptRunner`), so you register the script path rather
than a full interpreter command line.

## See also

- [commands.md](commands.md) — registering one-line inline commands
- [tools-and-permissions.md](tools-and-permissions.md) — per-tool permissions for the execute tools
- [settings-and-backup.md](settings-and-backup.md) — Project vs. User settings tiers
