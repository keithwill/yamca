using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MudBlazor.Services;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
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
builder.Services.AddSingleton<IWorkspace>(_ => new Workspace(workspaceRoot));

builder.Services.AddSingleton<ITool, ReadFileTool>();
builder.Services.AddSingleton<ITool, WriteFileTool>();
builder.Services.AddSingleton<ITool, EditFileTool>();
builder.Services.AddSingleton<ITool, DeleteFileTool>();
builder.Services.AddSingleton<ITool, ListDirectoryTool>();
builder.Services.AddSingleton<ITool, FindFilesTool>();
builder.Services.AddSingleton<ITool, GrepTool>();
builder.Services.AddSingleton<ITool, ExecuteCommandTool>();

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

// IToolRegistry is scoped so its enumeration of ITool services picks up both
// singleton tools and the per-circuit scoped script tools.
builder.Services.AddScoped<IToolRegistry>(sp => new ToolRegistry(sp.GetServices<ITool>()));

// Per-circuit (scoped) state — each browser tab gets its own settings, approval
// queue, and permission resolver.
builder.Services.AddScoped<SessionSettings>();
builder.Services.AddScoped<ISessionSettings>(sp => sp.GetRequiredService<SessionSettings>());
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
builder.Services.AddScoped<IApprovalCoordinator, ApprovalCoordinator>();
builder.Services.AddScoped<IPermissionStore, SessionSettingsPermissionStore>();

builder.Services.AddScoped<EndpointHealthService>();
builder.Services.AddScoped<LocalStorage>();
builder.Services.AddScoped<WorkspaceKey>();
builder.Services.AddScoped<SettingsHydrator>();
builder.Services.AddScoped<InstructionFilesLoader>();
builder.Services.AddScoped<WorkspaceBrowser>();
builder.Services.AddScoped<ChatViewModel>();

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
    writer.WriteLine();
    writer.WriteLine("Arguments:");
    writer.WriteLine("  workspace-path        Directory to sandbox the agent to (default: current directory).");
    writer.WriteLine();
    writer.WriteLine("Options:");
    writer.WriteLine("  -p, --port <n>        Listen on a specific port (default: 9001).");
    writer.WriteLine("      --no-browser      Do not open the default browser on startup.");
    writer.WriteLine("  -h, --help            Show this help and exit.");
    writer.WriteLine("      --version         Print the tool version and exit.");
}

internal sealed record CliOptions(
    string? WorkspacePath,
    int? Port,
    bool NoBrowser,
    bool ShowHelp,
    bool ShowVersion,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        string? workspace = null;
        int? port = null;
        bool noBrowser = false;
        bool help = false;
        bool version = false;
        string? error = null;

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

        return new CliOptions(workspace, port, noBrowser, help, version, error);
    }
}

public partial class Program;
