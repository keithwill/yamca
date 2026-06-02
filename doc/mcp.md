# MCP Servers

[Model Context Protocol](https://modelcontextprotocol.io) servers contribute
additional tools to the agent beyond yamca's built-in set. Manage them at `/mcp`.

## How MCP tools appear

Each connected server's tools are exposed to the agent as
`mcp__<id>__<tool>`. They are **deferred**: the schema never enters the prompt
prefix (preserving prompt-cache hits). The LLM discovers a tool with
`lookup_tool` and invokes it via `call_tool`. See
[tools-and-permissions.md](tools-and-permissions.md) for the availability model.

MCP tools resolve permissions through the same Project → User → default chain
as built-in tools.

## Configuration

Servers are defined in a config file (`McpConfigFileStore`), shown on the page as
its on-disk path. The configuration is **process-wide**: it's shared by every
chat session in the running yamca process, not scoped per workspace.

Each server entry can be **enabled** or disabled. A disabled server starts in the
`Disabled` status and contributes no tools.

## Connection lifecycle

Each enabled server gets one `McpServerConnection` that owns a connected client
and its adapted tool list:

- On startup yamca connects, performs the MCP handshake, and enumerates the
  server's tools (default startup timeout **20s**).
- Tool calls have a default timeout of **30s**, overridable per server via
  `CallTimeoutSeconds`.
- Status is tracked (`Connecting`, connected, `Disabled`, failed) along with a
  failure message when a connection fails.

## Diagnostics

Each server keeps a **log buffer** (`McpServerLogBuffer`) capturing its output,
surfaced in the UI so you can diagnose a server that fails to start or
handshake — the usual culprits being a bad command, missing dependency, or
protocol mismatch.

## See also

- [tools-and-permissions.md](tools-and-permissions.md) — availability (deferred) and permission model
