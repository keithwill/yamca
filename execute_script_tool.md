# Plan: `execute_script` Tool

## Motivation

The current `execute_command` tool wraps `cmd.exe /c` (Windows) and `/bin/sh -c` (Unix). Two problems flow from that:

1. **Shell-syntax dependency.** The LLM has to know two shell vocabularies, and the prompt has to teach the differences. Errors in quoting, redirection, and environment expansion are common and easy to get subtly wrong.
2. **Coarse permissions.** "Allow shell access" is effectively all-or-nothing. Users who want yamca to run their build can't grant that without also granting arbitrary command execution.

`execute_script` is a complementary tool — not a replacement — that takes a file-based approach: the model invokes scripts already present on disk, and yamca dispatches them to the correct interpreter. No shell parsing, no metacharacter interpretation, scoped permissions.

The longer-term direction this aligns with: yamca moves toward purely file-based operations (read, write, edit, run-script) and away from arbitrary shell access. `execute_command` remains available but becomes the escape hatch, not the default.

## Tool contract

**Name:** `execute_script`

**Parameters:**
- `script_path` (string, required) — path to the script, resolved relative to the workspace root. Must resolve inside the workspace when workspace restriction is enabled.
- `arguments` (array of strings, optional) — passed as argv to the script. Typed list, never a single string, to eliminate shell-injection paths.
- `timeout_seconds` (integer, optional, default 60, max 600) — same semantics as `execute_command`.

**Returns:** `exit_code`, `stdout`, `stderr` — same shape as `execute_command`.

**Workspace restriction:** `SupportsWorkspaceRestriction => true`. The resolved `script_path` must be inside the workspace root using the same canonicalization logic as `read_file` / `write_file`.

## Interpreter dispatch

Dispatch by extension. Resolve interpreter paths at tool invocation time (walk `PATH`); pass the resolved absolute path to `ProcessStartInfo.FileName` with `UseShellExecute = false`. No shell is ever in the loop.

| Extension       | Invocation                                                                    |
| --------------- | ----------------------------------------------------------------------------- |
| `.ps1`          | `pwsh -NoProfile -File <path> <args>`; Windows fallback to `powershell.exe`   |
| `.sh`           | Unix: invoke directly if executable (honor shebang); else `/bin/sh <path>`    |
| `.py`           | `python <path>` (resolve `python3` first on Unix, then `python`)              |
| `.js` / `.mjs`  | `node <path>`                                                                 |
| `.ts`           | Prefer `tsx`, then `ts-node`, then `bun`, then `deno run`. Error if none.     |
| `.cs`           | `dotnet run <path>` (requires .NET 10+ for loose-file support)                |

**Unix shebang preference.** On Unix, if the script file has the executable bit set, invoke it directly and let the kernel honor `#!`. Fall back to extension dispatch only when not executable (common on Windows-hosted checkouts mounted into WSL).

**Missing interpreter.** Return a clear, actionable error: `"Cannot run .ps1 script: neither 'pwsh' nor 'powershell' found on PATH."` The LLM can then surface the missing dependency to the user.

**No `.bat` / `.cmd`.** Skipped intentionally. Anyone authoring build scripts today is using PowerShell on Windows.

## Script registry

### Storage

Yamca settings live in browser `localStorage`, A new section is added per project (or global, though per project is the expected use-case):

```json
{
  "scripts": {
    "registered": [
      { "path": "build.ps1", "description": "Build the solution" },
      { "path": "test.ps1",  "description": "Run unit tests" },
      { "path": ".scripts/deploy.ps1", "description": "Deploy to staging" }
    ],
    "directories": [
      { "path": ".scripts", "description": "Project scripts directory" }
    ]
  }
}
```

- **No global defaults.** Yamca does not assume `.scripts/`, `scripts/`, `package.json` scripts, or any other convention.
- **Two registration shapes.** Individual files (`registered`) or whole directories (`directories`). Files inside a registered directory are treated as registered.
- **Descriptions (optional) are user-authored**, shown to the LLM at session start so it can choose the right script for a task.

Because settings live in `localStorage` rather than on disk, the LLM cannot self-promote scripts to the registry. There is no settings file in the workspace for it to discover or edit.

### Session-start system message

When `execute_script` is enabled, yamca injects a system message with the environment block at session start:

```
The execute_script tool is available. Prefer registered scripts for build,
test, and deploy operations over execute_command.

Registered scripts for this project:
  build.ps1      — Build the solution
  test.ps1       — Run unit tests
  .scripts/deploy.ps1 — Deploy to staging

Registered script directories:
  .scripts/      — Project scripts directory

Other script-shaped files found in the workspace are not registered. You may
propose running them, but the user will be asked to approve each invocation,
and they may choose to register the script for future use.

Do not create new scripts or modify registered scripts unless explicitly asked.
These are user-curated entry points.
```

If no scripts are registered, the message states that and notes the user can register scripts via the permissions UI.

## Permission tiers

Two tiers, both routed through the standard `Allow` / `Deny` / `Ask` model but with separate defaults:

| Tier         | Definition                                                | Sensible default        |
| ------------ | --------------------------------------------------------- | ----------------------- |
| Registered   | `script_path` matches a registered file or lives under a registered directory | `Allow` (workspace-scoped) |
| Discovered   | Script-shaped file the model found via other tools, not in the registry      | `Ask`                   |

The tool itself exposes two permission keys: `execute_script.registered` and `execute_script.discovered`. Project settings override global, same as other tools.

### Approval UX for discovered scripts

When a discovered script triggers an `Ask` prompt, the dialog shows:

- Resolved script path
- Dispatch decision: `pwsh -NoProfile -File ./tools/setup.ps1`
- Arguments (typed list, rendered verbatim)

Three actions:

1. **Allow once** — run this invocation; do not change settings.
2. **Allow and register** — run this invocation; add the script (or prompt for a description and add it) to the registry. Subsequent runs fall under the registered tier.
3. **Deny** — return an error to the model.

The "Allow and register" path is the key UX move: promoting a script from discovered to registered should be one click, not "approve now, then go edit settings later." Otherwise nothing ever leaves the noisy tier.

## Implementation notes

### Path resolution

Reuse the existing canonicalization logic from `read_file` / `write_file`:

- Resolve `script_path` relative to workspace root.
- Apply `Path.GetFullPath` and resolve symlinks.
- Reject if resolved path escapes the workspace root.
- Reject if the file does not exist or is not a regular file.

### Process startup

```csharp
var psi = new ProcessStartInfo
{
    FileName = resolvedInterpreterPath,   // absolute path from PATH lookup
    WorkingDirectory = workspaceRoot,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
};
foreach (var arg in interpreterArgs) psi.ArgumentList.Add(arg);  // e.g. -NoProfile -File
psi.ArgumentList.Add(resolvedScriptPath);
foreach (var arg in userArguments) psi.ArgumentList.Add(arg);
```

Output capture, timeout, and cancellation follow the existing `ExecuteCommandTool` pattern (`MaxStreamChars`, `BeginOutputReadLine`, linked CTS, `entireProcessTree: true` kill on cancel).

### Interpreter resolution

Helper that walks `PATH` (and `PATHEXT` on Windows) to find the first matching executable. Cache results per session — they don't change mid-run. Surface "interpreter not found" as a tool error, not an exception.

## Out of scope (for now)

- Streaming script output to the chat (current `execute_command` captures and returns at the end; same model here).
- Per-script timeout overrides in the registry.
- Environment variable injection or `.env` loading.
- Script composition / pipelines.

## Open questions

- Should registered scripts get a stable identifier (e.g., a short name like `build`) so the LLM calls `execute_script({ name: "build" })` rather than `execute_script({ script_path: "build.ps1" })`? Names are friendlier and decouple the LLM from filesystem layout, but add a layer of indirection users must maintain. Defer.
- Should `execute_command` be disabled by default when `execute_script` is enabled, to push users fully toward the file-based workflow? Probably a per-project setting; don't decide globally.
