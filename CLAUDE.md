# Yamca

Local-first Blazor agent / chat UI, shipped as a .NET 10 global tool. Run `yamca`
in any directory; that folder becomes the agent's sandboxed workspace, served on
`http://127.0.0.1:9001` (falls back to an OS-assigned port if 9001 is taken).

## Build & test

```sh
dotnet build yamca.slnx          # build the solution
dotnet test                      # run all tests (NUnit)
dotnet run --project Yamca.Web   # run the app locally (boots in Production)
```

- Target framework: `net10.0`, nullable + implicit usings enabled.
- Tests use **NUnit**; `Yamca.Agent` exposes internals to `Yamca.Agent.Tests`.

## Projects

- **Yamca.Agent** — the engine: tools, chat/agent loop, permissions, settings,
  board, orchestrator, subagents, MCP, git, workspace sandbox. No UI. Pure library.
- **Yamca.Web** — Blazor Server UI (MudBlazor) + the `yamca` CLI entry point
  (`Program.cs`). Packs as the global tool (`PackAsTool`, `ToolCommandName=yamca`).
- **Yamca.Agent.Tests**, **Yamca.Web.Tests** — NUnit test projects.

## Architecture notes

- **Composition root is `Yamca.Web/Program.cs`** — CLI parsing, port binding, and
  all DI wiring live here. Tools are registered as `ITool` services; read it to
  see lifetime intent (singleton vs. scoped) and why.
- **Service lifetimes matter.** Most tools are singletons, but tools that read
  per-session state (shell preference, permissions, registries) are **scoped** —
  one scope per browser circuit / chat session. `IToolRegistry`, `ISessionSettings`,
  `IPermissionResolver`, `LoadedToolSet`, subagent/loop runners are all scoped.
- **Workspace** (`IWorkspace`) splits `RootPath` (the sandbox boundary — every file
  op is clamped to it via `Resolve`) from `RepositoryRoot` (git top-level, where
  repo-scoped artifacts anchor). They differ when yamca opens a subdirectory of a repo.
- **Tools** implement `ITool` (`Yamca.Agent/Tools`). A tool exposes a JSON-schema,
  a `DefaultPermission`, and can be *deferred* (schema kept out of the prompt prefix,
  discovered via `lookup_tool` / invoked via `call_tool`) to preserve the prefix cache.
- **Permissions** — every tool call is gated Allow/Ask/Deny by `IPermissionResolver`;
  "Ask" routes through `IApprovalCoordinator` to a UI prompt.
- **Local state lives under `.yamca/`** at the repo root (gitignored, never committed):
  chat history (`.yamca/chat`), the dev board in the shared VestPocket store
  (`.yamca/yamca.db`), worktrees. User-tier settings and `mcp.json` live in the OS
  per-user config dir, out of the workspace.

## Conventions

- Match the surrounding code's style. This codebase favors **detailed explanatory
  comments** on non-obvious wiring (see `Program.cs`); keep that density where it earns it.
- Durable project knowledge goes in **`doc/*.md`** (also embedded into `Yamca.Web` and
  rendered as the in-app settings help modals) — not scattered in code.
- Version lives in `Yamca.Web/Yamca.Web.csproj` (`<Version>`).

## Docs

Feature docs in `doc/`: dev-board, orchestrator, chat-sessions, endpoints,
tools-and-permissions, subagents, loop, worktrees, mcp, scripts,
custom-instructions, settings-and-backup, commands.
