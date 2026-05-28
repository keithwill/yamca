# MCP Support for Yamca — Plan

## Goal

Let users plug arbitrary [Model Context Protocol](https://modelcontextprotocol.io) servers into Yamca so that their tools become callable by the agent alongside Yamca's built-ins. Built on top of the official `ModelContextProtocol` NuGet package (jointly maintained by Anthropic and Microsoft).

## Decisions already made

- **No curated catalog.** Yamca will not ship a list of recommended MCP servers. Users supply their own config.
- **Configuration storage:** MCP server configs live in `localStorage` alongside other Yamca settings — same persistence model as endpoint settings, permissions, etc.
- **Configuration entry UX:** the "Add MCP server" flow in the settings UI prompts the user to paste a JSON config block (the same shape they'd put in an `mcp.json` for Claude Desktop / Cursor / etc.). This is a deliberate concession to the ecosystem norm — every MCP server's README ships a JSON snippet, and we want users to be able to copy/paste rather than translate into a Yamca-specific form.
- **Lifecycle:** MCP servers are loaded **per process**, not per session. One set of `IMcpClient` instances is shared across all chat sessions in the running Yamca instance.

## Why "no catalog" isn't a deal-breaker

Cursor, Claude Desktop, Continue, and Zed all rely on a config file or paste-JSON dialog; none ships a meaningful first-party catalog. Users discover MCP servers via `modelcontextprotocol/servers` on GitHub, smithery.ai, or word of mouth. An MCP entry is small (command, args, env, or a URL), so a catalog mostly saves keystrokes for non-technical users — not Yamca's audience. If catalogs become table stakes later, integrating with smithery's API is a strict superset of the paste-JSON flow.

## Integration shape

Yamca's existing abstractions cover most of what an MCP host needs. Mapping:

1. **`McpServerConnection`** — wraps an `IMcpClient` from `ModelContextProtocol`. One per configured server. Owns lifecycle: spawn/connect, initialize, capability handshake, dispose. Tracks status (`Connecting | Ready | Failed | Disabled`) and a per-server log buffer.

2. **`McpToolAdapter : ITool`** — one adapter per `tools/list` entry returned by a connected server. Maps:
   - `Name` → `"mcp__<serverId>__<toolName>"`. The prefix prevents collisions across servers and gives the permissions UI a clean filter.
   - `Description` / `ParametersSchema` → passthrough from the server. No normalization.
   - `ExecuteAsync` → `client.CallToolAsync`, marshalling MCP content blocks (text / image / resource) into `ToolResult`.
   - `SupportsWorkspaceRestriction` → `false`. Workspace confinement only makes sense for in-process filesystem tools; MCP servers manage their own scope.
   - `DefaultPermission` → `Confirm`. MCP tools are third-party code.
   - `Deferred` → `true` by default. Yamca's existing `load_tool` mechanism keeps the initial tool list from ballooning when a user adds several servers totalling dozens of tools. This is a real advantage Yamca has that most clients lack.

3. **`McpRegistry`** — process-scoped singleton. Reads server configs from settings at startup, manages connections, exposes the current set of `McpToolAdapter` instances. Provides reload-on-config-change.

4. **`ToolRegistry` integration** — currently `ToolRegistry` is constructed from a one-shot `IEnumerable<ITool>` at DI time. To accommodate dynamic MCP tools, either:
   - Change `ToolRegistry` to accept a dynamic source it queries each time `GetChatTools` is called, or
   - Add a sibling "dynamic tools" channel that merges with the static set.

   The first option is cleaner; the second is less invasive. Decide during MVP implementation.

## Configuration model

`localStorage` key (e.g. `yamca.mcp.servers`) holds a JSON array:

```json
[
  {
    "id": "filesystem",
    "enabled": true,
    "config": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
      "env": { "FOO": "bar" }
    }
  },
  {
    "id": "fetch",
    "enabled": true,
    "config": {
      "url": "https://example.com/mcp",
      "headers": { "Authorization": "Bearer ..." }
    }
  }
]
```

The `config` object mirrors the de facto `mcp.json` shape (`command`/`args`/`env` for stdio, `url`/`headers` for HTTP) so users can paste straight from any server's README. Yamca infers transport from which fields are present.

## Settings UI

- New "MCP Servers" section on the settings page.
- List of configured servers with: enable toggle, status badge (Ready / Failed / Disabled), tool count, test-connection button, edit, remove.
- "+ Add server" opens a dialog with two fields: **id** (slug used in tool names) and a **JSON config block** textarea. Validate on submit; show parse errors inline.
- Per-server expand reveals its tools, each with the same permission / approval controls already used for built-ins (reusing `ToolPermissionSettings`).
- Per-server log buffer viewer (captures stderr from stdio servers and connection errors from HTTP servers).

## Lifecycle details

- **Per-process.** A single `McpRegistry` lives for the lifetime of the Yamca process. All chat sessions see the same set of MCP tools.
- **When to spawn.** Lazy on first session creation. Eager-at-boot adds startup latency for servers the user may not exercise in this run.
- **Reload semantics.** Editing/adding/removing a server in settings tears down the affected connection and re-spawns it. Other servers are untouched.
- **Failure isolation.** A server that fails to start (bad command, init error, crash) is marked `Failed` and skipped — chat sessions continue without its tools. The settings UI shows the failure and surfaces the log.
- **Cancellation / timeouts.** Every `CallToolAsync` wrapped in a hard timeout (30s default, configurable per server later). Servers can hang independently of the SDK's cancellation handling.

## Permissions / safety

- MCP tools execute third-party code that Yamca spawned or connects to. Two consequences:
  - Default `Confirm` permission — users must opt in per tool or per server before blanket-allowing.
  - Approval prompts label the tool as `[mcp: <serverId>]` so users don't mistake them for Yamca built-ins.
- MCP also defines **sampling** (server asks client to invoke the LLM) and **roots** (client advertises directories to the server). For MVP: decline sampling, expose only the workspace root.

## Risks

- **Spec churn.** Pin the `ModelContextProtocol` SDK version; revisit on minor bumps. The HTTP transport has changed across spec revisions; tools surface has been stable.
- **JSON Schema dialect mismatches.** A few servers emit schemas the LLM provider rejects. Pass through unchanged and let issues surface naturally; don't preemptively normalize.
- **Windows specifics.** `npx` / `uvx` shims can be finicky on Windows. Document the Node / uv PATH requirements. A "diagnostics" button that runs the configured command with `--version` would help triage.
- **stderr noise** from stdio servers flooding logs. Route to a per-server bounded log buffer surfaced in the settings UI.
- **Third-party trust.** Confirm-default is a partial mitigation; the README/settings UI should explicitly note that MCP servers run with the user's privileges.

## Phased rollout

### Phase 1 — MVP

- stdio transport only.
- Paste-JSON add-server dialog; configs persisted to `localStorage`.
- Tools only — no resources, prompts, or sampling.
- Per-process lifecycle, lazy spawn on first session.
- `Deferred = true` by default for MCP tools (uses existing `load_tool`).
- Approval prompts labeled with `[mcp: <serverId>]`.
- Dogfood against one well-behaved server (filesystem or fetch).

### Phase 2 — HTTP and polish ✅

- Streamable HTTP / SSE transport (auto-detected via `HttpClientTransport`).
- Settings UI: enable toggle, status badges, **edit**, remove, **test-connection** (restart), per-server log buffer.
- Per-server `timeoutSeconds` override in config JSON.
- Diagnostics button for stdio commands (runs `<command> --version` and shows stdout/stderr/exit code).

### Phase 3 — Beyond tools

- Resources — surface as `@`-mentions in the composer or a side panel.
- Prompts — slash-command-style quick inserts.

### Later / maybe

- Sampling (server calls LLM via Yamca).
- Smithery catalog integration if catalogs become an expected affordance.
- Per-session server overrides if real users ask for them.

## Open question to settle during Phase 1

- `ToolRegistry` dynamic-source refactor vs sibling dynamic channel — pick once we have the MCP adapter wired up and can see which shape fits the existing chat-tools call sites better.
