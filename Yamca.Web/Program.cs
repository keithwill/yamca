using MudBlazor.Services;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Agent.Workspace;
using Yamca.Web.Components;
using Yamca.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// --- Agent services -----------------------------------------------------------

// Workspace is bound once at process start to the directory `dotnet run` was
// invoked from. Per PLAN.md this is the sandbox root for the entire session.
builder.Services.AddSingleton<IWorkspace>(_ => new Workspace(Environment.CurrentDirectory));

builder.Services.AddSingleton<ITool, ReadFileTool>();
builder.Services.AddSingleton<ITool, WriteFileTool>();
builder.Services.AddSingleton<ITool, DeleteFileTool>();
builder.Services.AddSingleton<ITool, ListDirectoryTool>();
builder.Services.AddSingleton<ITool, ExecuteCommandTool>();
builder.Services.AddSingleton<IToolRegistry>(sp => new ToolRegistry(sp.GetServices<ITool>()));

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
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
