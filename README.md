# Yamca

Yamca is a local-first Blazor agent / chat UI distributed as a .NET global
tool. Launch `yamca` from any directory and the current folder becomes the
agent's sandboxed workspace.

## Requirements

- [.NET 10 runtime](https://dotnet.microsoft.com/download) (the ASP.NET Core
  runtime is sufficient — Yamca ships framework-dependent).

## Install

```sh
dotnet tool install --global Yamca
```

Update with:

```sh
dotnet tool update --global Yamca
```

Uninstall with:

```sh
dotnet tool uninstall --global Yamca
```

## Usage

```sh
yamca                       # workspace = current directory; serves on http://127.0.0.1:9001
yamca C:\path\to\project    # bind workspace to a specific path
yamca --port 5555           # listen on a different port
yamca --no-browser          # don't auto-open the browser
yamca --help                # show all flags
yamca --version             # print version
```

On startup Yamca prints the URL it is listening on, e.g.

```
Yamca listening on http://127.0.0.1:51234  (workspace: C:\Repos\yamca)
```

## Features

Each feature has its own page under [`doc/`](https://github.com/keithwill/yamca/tree/main/doc)
(links are absolute so they resolve on both GitHub and NuGet.org):

- **[Dev Board](https://github.com/keithwill/yamca/blob/main/doc/dev-board.md)** — a local, uncommitted Kanban scratchpad that drives your immediate work through AI-assisted steps.
- **[Chat Sessions](https://github.com/keithwill/yamca/blob/main/doc/chat-sessions.md)** — streaming, tool-using conversations (up to 4 at once), with persistence, compaction, and a split view.
- **[Endpoints](https://github.com/keithwill/yamca/blob/main/doc/endpoints.md)** — OpenAI-compatible LLM backends (llama.cpp, vllm, OpenAI, OpenRouter, …) with health checks.
- **[Tools & Permissions](https://github.com/keithwill/yamca/blob/main/doc/tools-and-permissions.md)** — the agent's file, search, execution, and code-intelligence tools, gated by an Allow/Ask/Deny permission model.
- **[Worktrees](https://github.com/keithwill/yamca/blob/main/doc/worktrees.md)** — isolated git worktrees for branch work, and the RootPath vs. RepositoryRoot split.
- **[MCP Servers](https://github.com/keithwill/yamca/blob/main/doc/mcp.md)** — Model Context Protocol servers that contribute additional tools to the agent.
- **[Scripts](https://github.com/keithwill/yamca/blob/main/doc/scripts.md)** — registered vs. discovered scripts, with distinct execution permissions.
- **[Custom Instructions](https://github.com/keithwill/yamca/blob/main/doc/custom-instructions.md)** — system prompt and instruction files folded into every session.
- **[Settings & Backup](https://github.com/keithwill/yamca/blob/main/doc/settings-and-backup.md)** — Project vs. User settings tiers, preferences, and user-settings export/import.

## Troubleshooting

- **Port already in use** — Yamca defaults to port 9001. If 9001 is taken
  Yamca falls back to an OS-assigned port and prints the URL it ends up on,
  so running it against several repos at once just works. An explicit
  `--port <n>` is honored exactly and errors if that port is unavailable.
  Settings persist on disk — user settings and the MCP server list in your
  OS user-config directory, project settings under the repo's `.yamca` — so
  they survive a port change and are shared across browsers.
- **Browser did not open** — visit the URL printed on startup, or pass
  `--no-browser` and open it yourself. The auto-open helper uses
  `xdg-open` on Linux, `open` on macOS, and the shell on Windows.
- **`yamca: command not found`** — make sure `~/.dotnet/tools` (Linux/macOS)
  or `%USERPROFILE%\.dotnet\tools` (Windows) is on your `PATH`.

## License

MIT — see [LICENSE](LICENSE).
