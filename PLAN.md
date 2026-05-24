# Plan: yamca — Minimal Local-LLM Coding Agent (Blazor Web UI, C#)

## Context

Build a minimal coding agent from scratch in an empty `C:\Repos\yamca` directory. Defining constraints from the user:

- **Local-LLM first**: built around OpenAI-compatible endpoints (llama.cpp, vllm). Most agents bury local inference behind hosted providers; this project flips that — the only first-class providers are local OpenAI-compatible endpoints.
- **Web UI over TUI**: Blazor instead of a terminal interface.
- **Browser-stored settings**: localStorage for endpoint config, system prompt, tool permissions. The server holds no persistent user state.
- **Minimal scope**: no MCP, no planning modes, no skills/extensions. Just chat, tools, and permissions.

The intended outcome is a runnable agent: `dotnet run` from a target workspace directory opens a browser-based chat that can drive a local model through tool calls (read/write/delete files, list dirs, run shell commands) with per-tool permission control.

## Stack decisions (from clarifying questions)

- **.NET 10**, **Blazor Web App** with Interactive Server render mode (server-side execution — required since tools touch the filesystem and shell).
- **Official `OpenAI` NuGet** package, configured with a custom `Endpoint` to point at the local server.
- **Sandbox model**: full path canonicalization (`Path.GetFullPath` + symlink resolution), reject any resolved path outside the workspace root.
- **Permission model**: per-tool `Allow` / `Deny` / `Ask`. `Ask` always prompts at runtime. The approval UI offers Allow or Deny plus optional "save as default for this project" or "save as default everywhere". **Project settings override global.**
- **Component Framework**: Utilize MudBlazor.

## Project layout

Single solution, two projects plus tests:

```
yamca/
  yamca.sln
  Yamca.Web/        # Blazor Web App: pages, components, JS interop, hosting
  Yamca.Agent/      # Agent core: tools, permissions, chat loop, OpenAI client wrapper
  Yamca.Agent.Tests/    
```

Splitting the agent core into its own library keeps the tool and permission logic unit-testable without spinning up Blazor.

The test project should use NUnit and focus on testing functionality of the agent by utilizing a faked LLM implementation.

## Core components

### 1. Workspace (`Yamca.Agent/Workspace/Workspace.cs`)

- Singleton `IWorkspace` bound at process start to `Environment.CurrentDirectory`.
- `Resolve(string requested)`:
  1. Combine with root if relative.
  2. `Path.GetFullPath` to canonicalize (resolves `..` and the OS resolves symlinks on access).
  3. Verify the canonical path starts with `RootPath` using `OrdinalIgnoreCase` on Windows.
  4. Throw `PathOutsideWorkspaceException` on escape.
- Exposed read-only `RootPath` is shown in the UI so the user always knows the scope.

### 2. Tool system (`Yamca.Agent/Tools/`)

`ITool` interface: `Name`, `Description`, JSON `Parameters` schema, `bool SupportsWorkspaceRestriction`, `Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)`.

Initial tools:
- `ReadFileTool` — sandboxable
- `WriteFileTool` — overwrite/create, sandboxable
- `DeleteFileTool` — sandboxable
- `ListDirectoryTool` — sandboxable
- `ExecuteCommandTool` — `Process.Start` with captured stdout/stderr/exit; not workspace-sandboxable (its own permission gates it)

`IToolRegistry` enumerates tools and produces OpenAI `ChatTool` definitions for the request. `SupportsWorkspaceRestriction` tells the UI whether to expose the "restrict to working directory" toggle for that tool.

### 3. Permission engine (`Yamca.Agent/Permissions/`)

- `enum PermissionLevel { Allow, Deny, Ask }`
- `IPermissionResolver.Resolve(toolName)` merges in order: **project settings → global settings → tool's built-in default** (usually `Ask`, except `ReadFileTool` which defaults to `Allow`).
- Per tool call:
  1. Resolve permission.
  2. `Deny` → return error message to the LLM so it can adapt.
  3. `Allow` → execute.
  4. `Ask` → raise an approval request awaited by the UI; UI returns `{ decision, persist: none|project|global }`. `ApprovalCoordinator` writes the persisted choice back into session settings and notifies the client to update localStorage.
- Approvals scoped per Blazor circuit via `Channel<ApprovalRequest>` so multiple chats don't cross-pollinate prompts.

### 4. Settings (`Yamca.Agent/Settings/` + JS interop)

Two localStorage keys:
- `yamca.global`
- `yamca.project.<sha256(workspace-path)>`

Shape:
```jsonc
{
  "endpoint": { "baseUrl": "http://localhost:8080/v1", "apiKey": "", "model": "qwen2.5-coder-32b" },
  "systemPrompt": "You are a coding assistant operating in {{workspace}}.",
  "tools": {
    "read_file":       { "permission": "Allow", "restrictToWorkspace": true },
    "write_file":      { "permission": "Ask",   "restrictToWorkspace": true },
    "delete_file":     { "permission": "Ask",   "restrictToWorkspace": true },
    "list_directory":  { "permission": "Allow", "restrictToWorkspace": true },
    "execute_command": { "permission": "Ask" }
  }
}
```

Flow: on page load, JS interop reads both keys and pushes them to a scoped `ISessionSettings` on the server. UI mutations write to localStorage and refresh the session store. Server never persists.

### 5. Chat / agent loop (`Yamca.Agent/Chat/`)

- `ChatSession` holds the ordered `List<ChatMessage>` (system, user, assistant, tool).
- Built on `OpenAI.Chat.ChatClient`, constructed with `OpenAIClientOptions { Endpoint = new Uri(settings.BaseUrl) }`.
- `AgentLoop.RunTurnAsync(userMessage)`:
  1. Append user message; ensure system prompt is at index 0.
  2. Call `CompleteChatStreamingAsync` with tool definitions from the registry.
  3. Push streaming tokens through a `Channel<ChatStreamEvent>` consumed by the Blazor component.
  4. If the assistant turn ends with tool calls: resolve permissions → execute → append `tool` messages → loop.
  5. Stop when the assistant produces a plain message or hits a configurable max-iteration safety cap.

### 6. UI (`Yamca.Web/Components/Pages/`)

- `/` **Chat**: message list, streaming output, inline collapsible tool-call cards (args + result), inline approval prompts for `Ask`.
- `/settings` **Settings**: endpoint (base URL, API key, model), system prompt editor, tool table (permission dropdown + "restrict to workspace" toggle for sandboxable tools), Global / Project tab toggle.
- `/about` **About**: shows workspace path, an endpoint health-check button (`GET {baseUrl}/models`), and the resolved per-tool permissions for the current project.

A scoped `ChatViewModel` per circuit owns the session and exposes message/approval observables.

## Key files to create

- `Yamca.Web/Program.cs` — Blazor Web App host, DI for agent services (scoped per circuit), interactive server render mode.
- `Yamca.Web/Components/Pages/{Chat,Settings,About}.razor`
- `Yamca.Web/Components/{ChatMessageView,ToolCallCard,ApprovalPrompt}.razor`
- `Yamca.Web/wwwroot/js/storage.js` — `getItem` / `setItem` / `removeItem` interop.
- `Yamca.Agent/Workspace/Workspace.cs`
- `Yamca.Agent/Tools/{ReadFile,WriteFile,DeleteFile,ListDirectory,ExecuteCommand}Tool.cs` + `ToolRegistry.cs`
- `Yamca.Agent/Permissions/{PermissionResolver,ApprovalCoordinator}.cs`
- `Yamca.Agent/Chat/{ChatSession,AgentLoop}.cs`
- `Yamca.Agent/Settings/{SessionSettings,EndpointSettings,ToolSettings}.cs`
- `Yamca.Agent.Tests/` — workspace path-escape, permission precedence, tool registry round-trip.

## Verification

1. **Build**: `dotnet build` at repo root succeeds on .NET 10 SDK.
2. **Sandbox tests**: `dotnet test` confirms `..` escapes, symlinks pointing outside, and absolute paths outside the root all throw `PathOutsideWorkspaceException`.
3. **Permission tests**: project `Deny` overrides global `Allow`; project unset falls through to global; `Ask` raises an approval event and respects the persistence flag.
4. **End-to-end smoke**:
   - Start `llama-server` or `vllm` locally on a known port.
   - `dotnet run --project Yamca.Web` from a small test workspace directory.
   - Open browser, configure endpoint on `/settings`, hit health-check on `/about`.
   - In `/`, send a prompt that requires a tool (e.g. "list files here, then read README.md").
   - Verify: streaming tokens appear incrementally, tool-call cards render, `Ask` tools trigger approval prompts, persisted Allow choices apply on subsequent calls without re-prompting.
5. **Cross-session check**: refresh the page → settings reload from localStorage; project-scoped settings still bound to the same workspace key.

## Explicitly out of scope

MCP, planning modes, skills, extensions, additional providers (Anthropic / OpenAI hosted / etc.), chat persistence across restarts, authentication / multi-user (assumes localhost single-user).
