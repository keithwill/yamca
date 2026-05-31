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

## Troubleshooting

- **Port already in use** — Yamca defaults to port 9001. If 9001 is taken
  Yamca exits with an error; pass `--port <n>` to pick a different one.
  Settings persist on disk — global settings and the MCP server list in your
  OS user-config directory, project settings under the repo's `.yamca` — so
  they survive a port change and are shared across browsers.
- **Browser did not open** — visit the URL printed on startup, or pass
  `--no-browser` and open it yourself. The auto-open helper uses
  `xdg-open` on Linux, `open` on macOS, and the shell on Windows.
- **`yamca: command not found`** — make sure `~/.dotnet/tools` (Linux/macOS)
  or `%USERPROFILE%\.dotnet\tools` (Windows) is on your `PATH`.

## License

MIT — see [LICENSE](LICENSE).
