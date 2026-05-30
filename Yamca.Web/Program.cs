using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MudBlazor.Services;
using Yamca.Agent.Board;
using Yamca.Agent.Chat;
using Yamca.Agent.Git;
using Yamca.Agent.Mcp;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.Board;
using Yamca.Agent.Tools.CodeIntel;
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
var repositoryRoot = await gitService
    .GetRepoRootAsync(workspaceRoot, CancellationToken.None)
    .ConfigureAwait(false) ?? workspaceRoot;

if (cli.Mode == CliMode.BoardReinit)
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var workspace = new Workspace(workspaceRoot, repositoryRoot);
    var worktree = new BoardWorktree(workspace, gitService, loggerFactory.CreateLogger<BoardWorktree>());
    var r = await worktree.ReinitAsync(cli.Wipe, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("Board reinitialized.");
    Console.WriteLine($"  Columns created:       {r.ColumnsCreated}");
    Console.WriteLine($"  Instructions restored: {r.InstructionsRestored}");
    Console.WriteLine($"  Cards preserved:       {r.CardsPreserved}");
    Console.WriteLine($"  Cards moved to idea:   {r.CardsMoved}");
    if (r.CardsWiped > 0) Console.WriteLine($"  Cards wiped:           {r.CardsWiped}");
    return 0;
}

// Fixed default port so browser localStorage (keyed by origin) persists across
// runs and explicit via --port if the user needs to move it.
const int DefaultPort = 9001;
var port = cli.Port ?? DefaultPort;
if (!IsTcpPortAvailable(port))
{
    Console.Error.WriteLine($"yamca: port {port} is already in use. Pass --port <n> to pick another.");
    return 1;
}
var url = $"http://127.0.0.1:{port}";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls(url);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddHttpClient();

// --- Agent services -----------------------------------------------------------
// Workspace is bound once at process start, either to a path supplied as the
// first positional CLI argument or, if none was supplied, to the directory the
// process was launched from. Per PLAN.md this is the sandbox root for the
// entire session.
builder.Services.AddSingleton<IWorkspace>(_ => new Workspace(workspaceRoot, repositoryRoot));
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<BoardService>();
// The board lives on the yamca-board orphan branch, mounted as a worktree at <repo>/.yamca/board.
// BoardWorktree owns that location and bootstrap, anchored at the root workspace's repository root
// (not any per-session workspace), so every chat session and the board UI share one canonical board.
// Created lazily on first board access (EnsureAsync), so an unused board costs nothing at startup.
builder.Services.AddSingleton<BoardWorktree>();

builder.Services.AddSingleton<ITool, ReadFileTool>();
builder.Services.AddSingleton<ITool, WriteFileTool>();
builder.Services.AddSingleton<ITool, EditFileTool>();
builder.Services.AddSingleton<ITool, DeleteFileTool>();
builder.Services.AddSingleton<ITool, ListDirectoryTool>();
builder.Services.AddSingleton<ITool, FindFilesTool>();
builder.Services.AddSingleton<ITool, GrepTool>();
builder.Services.AddSingleton<ITool, ExecuteCommandTool>();

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
builder.Services.AddScoped<ITool, LoadToolTool>();

// Dev board tools. Reads default to Allow; the mutating move/update tools default to Ask
// (like write_file/edit_file). They resolve the board through BoardWorktree and commit mutations
// to the yamca-board branch, independent of whichever code branch the session is on.
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

// MCP host: one registry per process, shared across all chat sessions. The web
// layer hydrates it from localStorage on first circuit and again whenever the
// user edits the MCP server list in settings.
builder.Services.AddSingleton<IMcpRegistry, McpRegistry>();
builder.Services.AddSingleton<IDynamicToolSource, McpDynamicToolSource>();

// IToolRegistry is scoped so its enumeration of ITool services picks up both
// singleton tools and the per-circuit scoped script tools. Dynamic sources
// (notably MCP) are merged in at query time.
builder.Services.AddScoped<IToolRegistry>(sp =>
    new ToolRegistry(sp.GetServices<ITool>(), sp.GetServices<IDynamicToolSource>()));

// Per-session set of deferred tools the LLM has loaded via load_tool. Scoped so
// each browser circuit / chat session starts with an empty set.
builder.Services.AddScoped<LoadedToolSet>();

// Per-circuit (scoped) state — each browser tab gets its own settings, approval
// queue, and permission resolver.
builder.Services.AddScoped<SessionSettings>();
builder.Services.AddScoped<ISessionSettings>(sp => sp.GetRequiredService<SessionSettings>());
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
builder.Services.AddScoped<IAvailabilityResolver, AvailabilityResolver>();
builder.Services.AddScoped<IApprovalCoordinator, ApprovalCoordinator>();
builder.Services.AddScoped<IPermissionStore, SessionSettingsPermissionStore>();

builder.Services.AddScoped<EndpointHealthService>();
builder.Services.AddTransient<ContextCompactor>();
builder.Services.AddScoped<LocalStorage>();
builder.Services.AddScoped<WorkspaceKey>();
builder.Services.AddScoped<SettingsHydrator>();
builder.Services.AddScoped<InstructionFilesLoader>();
builder.Services.AddScoped<WorkspaceBrowser>();
builder.Services.AddScoped<ChatSessionManager>();
builder.Services.AddScoped<BoardStepLauncher>();
builder.Services.AddScoped<McpConfigStore>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine($"Yamca listening on {url}  (workspace: {workspaceRoot})");
if (!string.Equals(repositoryRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
    Console.WriteLine($"  git repository root: {repositoryRoot}  (dev board and worktrees anchor here)");
if (!cli.NoBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(url));
}

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
    writer.WriteLine("  -p, --port <n>        Listen on a specific port (default: 9001).");
    writer.WriteLine("      --no-browser      Do not open the default browser on startup.");
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

        return new CliOptions(mode, workspace, port, noBrowser, wipe, help, version, error);
    }
}

public partial class Program;
