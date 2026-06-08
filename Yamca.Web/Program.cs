using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MudBlazor.Services;
using Yamca.Agent.Board;
using Yamca.Agent.Chat;
using Yamca.Agent.Chat.Persistence;
using Yamca.Agent.Git;
using Yamca.Agent.Mcp;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Settings.Persistence;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.Board;
using Yamca.Agent.Tools.CodeIntel;
using Yamca.Agent.Tools.Git;
using Yamca.Agent.Tools.ProcessManagement;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;
using Yamca.Agent.Workspace;
using Yamca.Web.Components;
using Yamca.Web.Services;

var cli = CliOptions.Parse(args);
if (cli.ShowHelp)
{
    PrintHelp();
    return 0;
}
if (cli.ShowVersion)
{
    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
    return 0;
}
if (cli.Error is not null)
{
    Console.Error.WriteLine($"yamca: {cli.Error}");
    PrintHelp(Console.Error);
    return 2;
}

var workspaceRoot = Environment.CurrentDirectory;
if (cli.WorkspacePath is not null)
{
    if (!Directory.Exists(cli.WorkspacePath))
    {
        Console.Error.WriteLine($"yamca: working directory '{cli.WorkspacePath}' does not exist or is not a directory.");
        return 1;
    }
    workspaceRoot = Path.GetFullPath(cli.WorkspacePath);
}

// The repository top-level, discovered once at startup. Repo-scoped artifacts (the dev board
// and worktrees) anchor here rather than at the sandbox root, so opening yamca in a subdirectory
// still finds the board and roots worktrees at the repo root. Falls back to the workspace root
// when not inside a git repository. The sandbox boundary stays at workspaceRoot regardless.
var gitService = new GitService();
var discoveredRepoRoot = await gitService
    .GetRepoRootAsync(workspaceRoot, CancellationToken.None)
    .ConfigureAwait(false);
var repositoryRoot = discoveredRepoRoot ?? workspaceRoot;

if (cli.Mode == CliMode.BoardReinit)
{
    var workspace = new Workspace(workspaceRoot, repositoryRoot);
    var boardStore = new BoardStore(workspace);
    var r = await boardStore.ReinitAsync(cli.Wipe, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("Board reinitialized.");
    Console.WriteLine($"  Columns created:       {r.ColumnsCreated}");
    Console.WriteLine($"  Instructions restored: {r.InstructionsRestored}");
    Console.WriteLine($"  Cards preserved:       {r.CardsPreserved}");
    Console.WriteLine($"  Cards moved to idea:   {r.CardsMoved}");
    if (r.CardsWiped > 0) Console.WriteLine($"  Cards wiped:           {r.CardsWiped}");
    return 0;
}

// Keep yamca's local-only state under .yamca (chat history, etc.) out of git without making the
// user edit their repo's root .gitignore. Only meaningful inside a git repository, so skip it when
// the workspace isn't one. Independent of the board/worktree bootstrap, which manage their own paths.
if (discoveredRepoRoot is not null)
    WorkspaceScaffold.EnsureGitignore(repositoryRoot);

// All settings now persist on disk (user tier in the user config dir, project tier under
// .yamca, MCP servers in mcp.json) keyed by path — nothing is keyed on the browser origin
// anymore, so the listening port no longer has to stay stable across runs. We still prefer
// 9001 (the "It's over 9000!" in-joke, and a bookmarkable default), but when it's already
// taken we bind an OS-assigned ephemeral port instead of failing, which makes running yamca
// against several repos at once frictionless. An explicit --port is honored exactly and
// errors when unavailable.
const int DefaultPort = 9001;
int bindPort;
if (cli.Port is int requested)
{
    if (!IsTcpPortAvailable(requested))
    {
        Console.Error.WriteLine($"yamca: port {requested} is already in use. Pass --port <n> to pick another.");
        return 1;
    }
    bindPort = requested;
}
else
{
    // Prefer the default; fall back to port 0 (OS-assigned) when it's busy. With an ephemeral
    // port the real value isn't known until Kestrel has started, so the listening URL is
    // resolved after startup rather than computed here.
    bindPort = IsTcpPortAvailable(DefaultPort) ? DefaultPort : 0;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls($"http://127.0.0.1:{bindPort}");

// Load the static-web-assets manifest explicitly. The framework only auto-loads the dev manifest
// (Yamca.Web.staticwebassets.runtime.json — which maps MudBlazor's CSS/JS, blazor.web.js and the
// scoped-CSS bundle from their source folders) when the environment is Development. We dropped
// launchSettings.json, so `dotnet run` now boots in Production and that auto-load is skipped,
// leaving MapStaticAssets() with nothing to serve and the UI without its CSS/JS. Calling this here
// forces the load regardless of environment; it no-ops when the manifest is absent (the packed
// global tool, which serves a real published wwwroot instead), so it's safe in both modes.
builder.WebHost.UseStaticWebAssets();

// Log levels live in code, not appsettings.json: yamca ships as a .NET global tool, so its
// content files sit behind the tool-shim install directory where no user would ever go to edit
// them. `--verbose` drops the default floor from Information to Debug; the framework categories
// stay at Warning to keep ASP.NET/HttpClient request chatter out of the console.
builder.Logging.AddFilter(null, cli.Verbose ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Pasted images arrive as a single base64 JS->.NET argument that exceeds SignalR's
    // 32 KB default and would tear down the circuit. yamca runs on localhost for a single
    // user, so there's no transport budget to defend — disable the message-size cap.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = null);

builder.Services.AddMudServices();

builder.Services.AddHttpClient();

// Shared "endpoint → configured HttpClient → OpenAIChatCompletionClient" factory, so the chat
// loop, subagent runner, and context compactor all build their LLM client identically.
builder.Services.AddSingleton<EndpointClientFactory>();

// --- Agent services -----------------------------------------------------------
// Workspace is bound once at process start, either to a path supplied as the
// first positional CLI argument or, if none was supplied, to the directory the
// process was launched from. Per PLAN.md this is the sandbox root for the
// entire session.
builder.Services.AddSingleton<IWorkspace>(_ => new Workspace(workspaceRoot, repositoryRoot));
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<BoardService>();
// The board is a plain, uncommitted directory at <repo>/.yamca/board — a personal scratchpad,
// gitignored and never tracked or pushed. BoardStore owns that location and bootstrap, anchored at
// the root workspace's repository root (not any per-session workspace), so every chat session and
// the board UI share one canonical board. Created lazily on first board access (EnsureAsync), so an
// unused board costs nothing at startup.
builder.Services.AddSingleton<BoardStore>();

builder.Services.AddSingleton<ITool, ReadFileTool>();
builder.Services.AddSingleton<ITool, WriteFileTool>();
builder.Services.AddSingleton<ITool, EditFileTool>();
builder.Services.AddSingleton<ITool, DeleteFileTool>();
builder.Services.AddSingleton<ITool, ListDirectoryTool>();
builder.Services.AddSingleton<ITool, FindFilesTool>();
builder.Services.AddSingleton<ITool, GrepTool>();
// Scoped (not singleton like its file-tool neighbors): execute_command reads the per-circuit
// ISessionSettings to honor the user's configured shell, which is a scoped service.
builder.Services.AddScoped<ITool, ExecuteCommandTool>();

builder.Services.AddSingleton<ISymbolExtractor, CSharpSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, PythonSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, JavaScriptSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, TypeScriptSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, TsxSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, RustSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, GoSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, JavaSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, CSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, RubySymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, CppSymbolExtractor>();
builder.Services.AddSingleton<ISymbolExtractor, PhpSymbolExtractor>();
builder.Services.AddSingleton<SymbolService>();

// AST node-kind profiles for the code_find_* / code_search tools. The generic profile is
// the fallback for any routed language without a dedicated entry (hybrid coverage).
builder.Services.AddSingleton<ILanguageNodeProfile, GenericNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, CSharpNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, PythonNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, JavaScriptNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, TypeScriptNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, TsxNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, RustNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, GoNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, JavaNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, CNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, RubyNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, CppNodeProfile>();
builder.Services.AddSingleton<ILanguageNodeProfile, PhpNodeProfile>();
builder.Services.AddSingleton<NodeProfileResolver>();

builder.Services.AddSingleton<ITool, CodeListSymbolsTool>();
builder.Services.AddSingleton<ITool, CodeExtractSymbolTool>();
builder.Services.AddSingleton<ITool, CodeSurroundingContextTool>();
builder.Services.AddSingleton<ITool, CodeFindDefinitionsTool>();
builder.Services.AddSingleton<ITool, CodeFindCallsTool>();
builder.Services.AddSingleton<ITool, CodeFindReferencesTool>();
builder.Services.AddSingleton<ITool, CodeSearchTool>();
builder.Services.AddSingleton<ITool, CodeEditSymbolTool>();
// Deferred-tool dispatcher: lookup_tool surfaces schemas as tool-result content, call_tool
// invokes them. Both stay in the prefix for the whole session; deferred tool schemas never do,
// which is what preserves the prompt-prefix cache. Scoped because lookup_tool reads the
// per-session LoadedToolSet.
builder.Services.AddScoped<ITool, LookupToolTool>();
builder.Services.AddScoped<ITool, CallToolTool>();

// Dev board tools. Reads default to Allow; the mutating move/update tools default to Ask
// (like write_file/edit_file). They resolve the board through BoardStore — a plain directory at the
// repository root, shared by every chat session independent of which code branch it is on.
builder.Services.AddSingleton<ITool, BoardListTool>();
builder.Services.AddSingleton<ITool, BoardGetCardTool>();
builder.Services.AddSingleton<ITool, BoardGetStepInstructionsTool>();
builder.Services.AddSingleton<ITool, BoardMoveCardTool>();
builder.Services.AddSingleton<ITool, BoardUpdateCardTool>();
builder.Services.AddSingleton<ITool, BoardReinitTool>();

// Script-tool collaborators. InterpreterResolver / ScriptRunner are stateless apart
// from a PATH-resolution cache, so they live as singletons. ScriptRegistryLookup
// reads scoped session settings.
builder.Services.AddSingleton<InterpreterResolver>();
builder.Services.AddSingleton<ShellResolver>();
builder.Services.AddSingleton<ScriptRunner>();
builder.Services.AddScoped<ScriptRegistryLookup>();
builder.Services.AddScoped<ITool, ExecuteRegisteredScriptTool>();
builder.Services.AddScoped<ITool, ExecuteDiscoveredScriptTool>();
builder.Services.AddScoped<ITool, ExecuteScriptTool>();

// Git tool. One LLM-facing 'git' tool runs a curated set of subcommands; it resolves the real
// permission under the git_read / git_write identities (the two rows shown in settings). Scoped
// so the facade's IServiceProvider is the per-session scope that owns IPermissionResolver.
builder.Services.AddScoped<ITool, GitReadTool>();
builder.Services.AddScoped<ITool, GitWriteTool>();
builder.Services.AddScoped<ITool, GitTool>();

// Subagents: subagent_run lets a chat delegate a task to a headless subagent session driven by
// SubagentRunner. Both are scoped — the runner reads per-circuit settings and is bound with the
// parent chat's completion client by ChatViewModel so subagents inherit the parent's endpoint.
builder.Services.AddScoped<ISubagentRunner, SubagentRunner>();
builder.Services.AddScoped<ITool, SubagentRunTool>();

// loop fans one prompt over many items, each its own headless subagent run, via BatchRunner
// (which reuses SubagentRunner) and returns a single mechanical roll-up. Scoped like the runner.
builder.Services.AddScoped<IBatchRunner, BatchRunner>();
builder.Services.AddScoped<ITool, LoopTool>();

// Background processes: one process-wide manager owns long-lived child processes that outlive the
// chat session that started them. BackgroundProcessHost (IHostedService) stops them all gracefully
// on app shutdown so started dev servers are not orphaned. The four tools are deferred (schemas stay
// out of the prompt prefix) and scoped, reading the per-circuit ISessionSettings shell preference.
builder.Services.AddSingleton<BackgroundProcessManager>();
builder.Services.AddSingleton<IBackgroundProcessManager>(sp => sp.GetRequiredService<BackgroundProcessManager>());
builder.Services.AddHostedService<BackgroundProcessHost>();
// start_process is an LLM-facing facade (hidden from the settings table): it runs registered inline
// commands under the execute_registered_script permission and arbitrary commands under the
// start_process_command identity (the Ask-by-default settings row). Scoped so the facade's
// IServiceProvider is the per-session scope owning IPermissionResolver.
builder.Services.AddScoped<ITool, StartProcessCommandTool>();
builder.Services.AddScoped<ITool, StartProcessTool>();
builder.Services.AddScoped<ITool, GetProcessOutputTool>();
builder.Services.AddScoped<ITool, StopProcessTool>();
builder.Services.AddScoped<ITool, ListProcessesTool>();

// Live subagent transcripts: the runner mirrors each run's event stream into this per-circuit
// registry via ISubagentObserver; the UI reads the same instance to render a read-only view.
builder.Services.AddScoped<SubagentSessionRegistry>();
builder.Services.AddScoped<ISubagentObserver>(sp => sp.GetRequiredService<SubagentSessionRegistry>());

// MCP host: one registry per process, shared across all chat sessions. The web
// layer hydrates it from mcp.json on first circuit and again whenever the
// user edits the MCP server list in settings.
builder.Services.AddSingleton<IMcpRegistry, McpRegistry>();
builder.Services.AddSingleton<IDynamicToolSource, McpDynamicToolSource>();
// The configured server list persists to mcp.json in the per-user config directory (out of
// the LLM-reachable workspace), alongside the user settings file. Singleton: one in-process
// write lock, matching the process-wide registry it feeds.
builder.Services.AddSingleton<McpConfigFileStore>(_ => new McpConfigFileStore(UserConfigDirectory.Resolve()));

// IToolRegistry is scoped so its enumeration of ITool services picks up both
// singleton tools and the per-circuit scoped script tools. Dynamic sources
// (notably MCP) are merged in at query time.
builder.Services.AddScoped<IToolRegistry>(sp =>
    new ToolRegistry(sp.GetServices<ITool>(), sp.GetServices<IDynamicToolSource>()));

// Per-session set of deferred tools whose schemas the LLM has seen (via lookup_tool or a
// call_tool self-correction). Scoped so each browser circuit / chat session starts empty.
builder.Services.AddScoped<LoadedToolSet>();

// Per-circuit (scoped) state — each browser tab gets its own settings, approval
// queue, and permission resolver.
builder.Services.AddScoped<SessionSettings>();
builder.Services.AddScoped<ISessionSettings>(sp => sp.GetRequiredService<SessionSettings>());
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
builder.Services.AddScoped<IAvailabilityResolver, AvailabilityResolver>();
builder.Services.AddScoped<IApprovalCoordinator, ApprovalCoordinator>();
builder.Services.AddScoped<IPermissionStore, SessionSettingsPermissionStore>();
builder.Services.AddScoped<AgentLoopFactory>();

builder.Services.AddScoped<EndpointHealthService>();
builder.Services.AddTransient<ContextCompactor>();
builder.Services.AddScoped<SettingsLocation>();
// User-tier settings persistence. Singleton (no per-circuit state) so one in-process lock
// serializes writes across circuits; the file lives in the OS per-user config directory,
// out of the LLM-reachable workspace.
builder.Services.AddSingleton<UserSettingsStore>(_ => new UserSettingsStore(UserSettingsStore.ResolveDefaultDirectory()));
// Project-tier settings persistence. Scoped, but resolves the singleton IWorkspace so the
// settings file always anchors to the main repository root (.yamca/project.json).
builder.Services.AddScoped<ProjectSettingsStore>();
builder.Services.AddScoped<SettingsHydrator>();
builder.Services.AddScoped<InstructionFilesLoader>();
builder.Services.AddScoped<WorkspaceBrowser>();
// Chat history persistence. Scoped, but resolves the singleton IWorkspace so chat files
// always anchor to the main repository root (.yamca/chat) — not a per-session worktree.
builder.Services.AddScoped<ChatStore>();
builder.Services.AddScoped<ChatSessionManager>();
builder.Services.AddScoped<BoardStepLauncher>();
builder.Services.AddScoped<McpConfigStore>();

var app = builder.Build();

// yamca is a single-user localhost tool whose one user is also the admin, so there's nobody to
// hide exception detail from: always show the full developer error page. (No HSTS either — the app
// only ever serves http on the loopback interface.)
app.UseDeveloperExceptionPage();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Graceful shutdown landing page. The sidebar's Exit action force-navigates the browser here,
// which tears down the Blazor circuit cleanly (no "reconnecting" overlay) and lands on plain
// static HTML that owns no circuit of its own — so stopping the host below can't surface a
// connection error on screen. The response is fully flushed before the host is asked to stop a
// short moment later; the page then polls the root URL and, once the server is gone, swaps its
// message to confirm shutdown.
app.MapGet("/goodbye", (IHostApplicationLifetime lifetime) =>
{
    // Stop after the response has flushed to the browser. Detached on purpose — the request
    // completes immediately and the host winds down a beat later.
    _ = Task.Run(async () =>
    {
        await Task.Delay(750).ConfigureAwait(false);
        lifetime.StopApplication();
    });
    return Results.Content(GoodbyeHtml(), "text/html; charset=utf-8");
});

// The bound port may be OS-assigned (ephemeral fallback), so the actual listening URL isn't
// known until Kestrel has started. Resolve it from the server's bound addresses once started,
// then announce it and open the browser.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{bindPort}";
    Console.WriteLine($"Yamca listening on {url}  (workspace: {workspaceRoot})");
    if (!string.Equals(repositoryRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"  git repository root: {repositoryRoot}  (dev board and worktrees anchor here)");
    if (!cli.NoBrowser)
        OpenBrowser(url);
});

app.Run();
return 0;

static bool IsTcpPortAvailable(int port)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"yamca: could not open browser ({ex.Message}). Visit {url} manually.");
    }
}

// Self-contained shutdown page: no Blazor, no external assets, so it survives the host stopping
// underneath it. It polls the root URL and, once the fetch fails (server gone), swaps the status
// line to confirm the process has fully exited.
static string GoodbyeHtml() =>
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>yamca — shutting down</title>
      <style>
        html, body { height: 100%; margin: 0; }
        body {
          display: flex; align-items: center; justify-content: center;
          background: #1a1a27; color: #e0e0e6;
          font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
        }
        .card { text-align: center; max-width: 30rem; padding: 2rem; }
        h1 { font-weight: 600; font-size: 1.5rem; margin: 0 0 0.75rem; }
        p { margin: 0.4rem 0; line-height: 1.5; color: #b5b5c2; }
        #status { margin-top: 1.25rem; font-weight: 600; color: #8a8aa0; }
        #status.stopped { color: #66bb6a; }
      </style>
    </head>
    <body>
      <div class="card">
        <h1>yamca is shutting down</h1>
        <p>The server process started in your terminal is closing. Your chats remain saved in history.</p>
        <p>To use yamca again, run <code>yamca</code> from your terminal.</p>
        <p id="status">Stopping the server…</p>
      </div>
      <script>
        function ping() {
          fetch('/', { cache: 'no-store' })
            .then(function () { setTimeout(ping, 800); })
            .catch(function () {
              var s = document.getElementById('status');
              s.textContent = 'Server stopped — you can safely close this tab.';
              s.classList.add('stopped');
            });
        }
        setTimeout(ping, 1000);
      </script>
    </body>
    </html>
    """;

static void PrintHelp(TextWriter? writer = null)
{
    writer ??= Console.Out;
    writer.WriteLine("Usage: yamca [workspace-path] [options]");
    writer.WriteLine("       yamca board reinit [workspace-path] [--wipe]");
    writer.WriteLine();
    writer.WriteLine("Commands:");
    writer.WriteLine("  board reinit          Restore the default column structure of the dev board.");
    writer.WriteLine("                        Cards in non-default columns are moved to idea.");
    writer.WriteLine("                        Pass --wipe to delete all cards instead.");
    writer.WriteLine();
    writer.WriteLine("Arguments:");
    writer.WriteLine("  workspace-path        Directory to sandbox the agent to (default: current directory).");
    writer.WriteLine();
    writer.WriteLine("Options:");
    writer.WriteLine("  -p, --port <n>        Listen on a specific port (default: 9001, or an");
    writer.WriteLine("                        OS-assigned port when 9001 is already in use).");
    writer.WriteLine("      --no-browser      Do not open the default browser on startup.");
    writer.WriteLine("  -v, --verbose         Enable debug-level logging.");
    writer.WriteLine("      --wipe            (board reinit) Delete all cards instead of moving to idea.");
    writer.WriteLine("  -h, --help            Show this help and exit.");
    writer.WriteLine("      --version         Print the tool version and exit.");
}

internal enum CliMode { Default, BoardReinit }

internal sealed record CliOptions(
    CliMode Mode,
    string? WorkspacePath,
    int? Port,
    bool NoBrowser,
    bool Verbose,
    bool Wipe,
    bool ShowHelp,
    bool ShowVersion,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var mode = CliMode.Default;
        string? workspace = null;
        int? port = null;
        bool noBrowser = false;
        bool verbose = false;
        bool wipe = false;
        bool help = false;
        bool version = false;
        string? error = null;
        bool inBoardSubcommand = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "--no-browser":
                    noBrowser = true;
                    break;
                case "-v":
                case "--verbose":
                    verbose = true;
                    break;
                case "--wipe":
                    wipe = true;
                    break;
                case "-p":
                case "--port":
                    if (i + 1 >= args.Length)
                    {
                        error = $"option '{a}' requires a value";
                        break;
                    }
                    if (!int.TryParse(args[++i], out var p) || p < 1 || p > 65535)
                    {
                        error = $"invalid port '{args[i]}'";
                        break;
                    }
                    port = p;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        error = $"unknown option '{a}'";
                    }
                    else if (!inBoardSubcommand && a == "board")
                    {
                        inBoardSubcommand = true;
                    }
                    else if (inBoardSubcommand && mode == CliMode.Default)
                    {
                        if (a == "reinit")
                            mode = CliMode.BoardReinit;
                        else
                            error = $"unknown board subcommand '{a}'";
                    }
                    else if (workspace is null)
                    {
                        workspace = a;
                    }
                    else
                    {
                        error = $"unexpected positional argument '{a}'";
                    }
                    break;
            }
            if (error is not null) break;
        }

        return new CliOptions(mode, workspace, port, noBrowser, verbose, wipe, help, version, error);
    }
}

public partial class Program;
