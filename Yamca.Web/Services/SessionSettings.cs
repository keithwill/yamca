using System.Text.Json;
using System.Text.Json.Serialization;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;

namespace Yamca.Web.Services;

/// <summary>
/// Concrete <see cref="ISessionSettings"/> implementation backing one Blazor circuit.
/// Mutations raise <see cref="Changed"/> so the UI can persist the affected tier to
/// localStorage. The server itself never persists anything.
/// </summary>
public sealed class SessionSettings : ISessionSettings
{
    private const string DefaultSystemPrompt =
        "You are a coding assistant.";

    public static readonly IReadOnlyList<string> DefaultGlobalInstructionFiles =
        new[] { "AGENTS.md", "CLAUDE.md", "GEMINI.md" };

    public ToolSettingsMap Project { get; private set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap Global { get; private set; } = ToolSettingsMap.Empty;
    public EndpointsSettings Endpoints { get; private set; } = EndpointsSettings.CreateDefault();
    public string SystemPrompt { get; private set; } = DefaultSystemPrompt;
    public bool MarkdownEnabled { get; private set; } = true;
    public ReasoningDisplay ReasoningDisplay { get; private set; } = ReasoningDisplay.Collapsed;

    public IReadOnlyList<string> GlobalInstructionFiles { get; private set; } = DefaultGlobalInstructionFiles;
    public IReadOnlyList<string> ProjectInstructionFiles { get; private set; } = Array.Empty<string>();
    public bool ProjectInheritsGlobalInstructions { get; private set; } = true;

    public ScriptRegistry GlobalScripts { get; private set; } = ScriptRegistry.Empty;
    public ScriptRegistry ProjectScripts { get; private set; } = ScriptRegistry.Empty;

    /// <summary>Fired when the named tier has been mutated. The handler is expected
    /// to serialize that tier and write it to localStorage.</summary>
    public event Action<SettingsTier>? Changed;

    // --- mutation API (used by Settings page + IPermissionStore adapter) -----------

    /// <summary>Append a new endpoint. The first endpoint added to an empty list
    /// becomes the default automatically.</summary>
    public void AddEndpoint(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var items = Endpoints.Items.Append(endpoint).ToList();
        var defaultId = Endpoints.Items.Count == 0 ? endpoint.Id : Endpoints.DefaultId;
        Endpoints = new EndpointsSettings(items, defaultId);
        Changed?.Invoke(SettingsTier.Global);
    }

    /// <summary>Replace the endpoint with the matching <see cref="EndpointSettings.Id"/>.
    /// Does nothing if no entry matches.</summary>
    public void UpdateEndpoint(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var idx = -1;
        for (var i = 0; i < Endpoints.Items.Count; i++)
        {
            if (Endpoints.Items[i].Id == endpoint.Id) { idx = i; break; }
        }
        if (idx < 0) return;
        var items = Endpoints.Items.ToList();
        items[idx] = endpoint;
        Endpoints = Endpoints with { Items = items };
        Changed?.Invoke(SettingsTier.Global);
    }

    /// <summary>Remove the endpoint with the given id. Refuses to remove the last
    /// remaining endpoint. If the default is removed, the first remaining entry
    /// becomes the new default.</summary>
    public void RemoveEndpoint(Guid id)
    {
        if (Endpoints.Items.Count <= 1) return;
        var items = Endpoints.Items.Where(e => e.Id != id).ToList();
        if (items.Count == Endpoints.Items.Count) return; // nothing matched
        var defaultId = Endpoints.DefaultId == id ? items[0].Id : Endpoints.DefaultId;
        Endpoints = new EndpointsSettings(items, defaultId);
        Changed?.Invoke(SettingsTier.Global);
    }

    public void SetDefaultEndpoint(Guid id)
    {
        if (Endpoints.DefaultId == id) return;
        if (Endpoints.FindById(id) is null) return;
        Endpoints = Endpoints with { DefaultId = id };
        Changed?.Invoke(SettingsTier.Global);
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        SystemPrompt = systemPrompt ?? string.Empty;
        Changed?.Invoke(SettingsTier.Global);
    }

    public void SetMarkdownEnabled(bool enabled)
    {
        if (MarkdownEnabled == enabled) return;
        MarkdownEnabled = enabled;
        Changed?.Invoke(SettingsTier.Global);
    }

    public void SetReasoningDisplay(ReasoningDisplay display)
    {
        if (ReasoningDisplay == display) return;
        ReasoningDisplay = display;
        Changed?.Invoke(SettingsTier.Global);
    }

    public void SetInstructionFiles(SettingsTier tier, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Select(p => p?.Trim() ?? string.Empty).ToArray();

        if (tier == SettingsTier.Project) ProjectInstructionFiles = normalized;
        else GlobalInstructionFiles = normalized;

        Changed?.Invoke(tier);
    }

    public void SetProjectInheritsGlobalInstructions(bool inherits)
    {
        if (ProjectInheritsGlobalInstructions == inherits) return;
        ProjectInheritsGlobalInstructions = inherits;
        Changed?.Invoke(SettingsTier.Project);
    }

    public void SetScripts(SettingsTier tier, ScriptRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (tier == SettingsTier.Project) ProjectScripts = registry;
        else GlobalScripts = registry;
        Changed?.Invoke(tier);
    }

    /// <summary>Append a single script entry to the named tier. Existing entries with
    /// the same path are replaced. Used by the approval prompt's "Allow and register"
    /// action.</summary>
    public void AddRegisteredScript(SettingsTier tier, RegisteredScript entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var current = tier == SettingsTier.Project ? ProjectScripts : GlobalScripts;
        var registered = current.Registered
            .Where(e => !string.Equals(e.Path, entry.Path, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        SetScripts(tier, new ScriptRegistry(registered, current.Directories));
    }

    /// <summary>Replace a single tool entry in the given tier, or pass <c>null</c> to remove.</summary>
    public void SetToolEntry(SettingsTier tier, string toolName, ToolPermissionSettings? entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        var source = tier == SettingsTier.Project ? Project : Global;
        var next = new Dictionary<string, ToolPermissionSettings>(source.Entries, StringComparer.Ordinal);

        if (entry is null)
            next.Remove(toolName);
        else
            next[toolName] = entry;

        var map = new ToolSettingsMap(next);
        if (tier == SettingsTier.Project) Project = map;
        else Global = map;

        Changed?.Invoke(tier);
    }

    // --- (de)serialization for localStorage ----------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Hydrate the global tier from a JSON blob read out of localStorage.
    /// Missing fields fall back to defaults. Silently ignores malformed input.</summary>
    public void HydrateGlobal(string? json)
    {
        var firstRun = string.IsNullOrWhiteSpace(json);
        var blob = TryDeserialize<GlobalBlob>(json) ?? new GlobalBlob();

        Endpoints = EndpointsFromBlob(blob);

        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        MarkdownEnabled = blob.MarkdownEnabled ?? true;
        ReasoningDisplay = blob.ReasoningDisplay ?? ReasoningDisplay.Collapsed;
        Global = firstRun ? DefaultGlobalToolSettings() : MapFromDto(blob.Tools);
        GlobalInstructionFiles = firstRun
            ? DefaultGlobalInstructionFiles
            : (blob.InstructionFiles?.ToArray() ?? Array.Empty<string>());
        GlobalScripts = ScriptsFromDto(blob.Scripts);
    }

    // Seeded only when no Global blob has ever been written to localStorage.
    // Once the user has stored anything, their choices — including explicit "inherit"
    // (a removed entry) — are respected verbatim.
    private static ToolSettingsMap DefaultGlobalToolSettings()
    {
        static ToolPermissionSettings Workspace(PermissionLevel p) =>
            new() { Permission = p, RestrictToWorkspace = true };

        return new ToolSettingsMap(new Dictionary<string, ToolPermissionSettings>(StringComparer.Ordinal)
        {
            ["read_file"]                = Workspace(PermissionLevel.Allow),
            ["write_file"]               = Workspace(PermissionLevel.Allow),
            ["delete_file"]              = Workspace(PermissionLevel.Allow),
            ["list_directory"]           = Workspace(PermissionLevel.Allow),
            ["execute_command"]          = new ToolPermissionSettings { Permission = PermissionLevel.Ask },
            ["execute_registered_script"] = Workspace(PermissionLevel.Allow),
            ["execute_discovered_script"] = Workspace(PermissionLevel.Ask),
        });
    }

    public void HydrateProject(string? json)
    {
        var blob = TryDeserialize<ProjectBlob>(json) ?? new ProjectBlob();
        Project = MapFromDto(blob.Tools);
        ProjectInstructionFiles = blob.InstructionFiles?.ToArray() ?? Array.Empty<string>();
        ProjectInheritsGlobalInstructions = blob.InheritsGlobalInstructions ?? true;
        ProjectScripts = ScriptsFromDto(blob.Scripts);
    }

    public string SerializeGlobal()
    {
        var blob = new GlobalBlob
        {
            Endpoints = EndpointsToDto(Endpoints, includeApiKeys: true),
            DefaultEndpointId = Endpoints.DefaultId,
            SystemPrompt = SystemPrompt,
            MarkdownEnabled = MarkdownEnabled,
            ReasoningDisplay = ReasoningDisplay,
            Tools = MapToDto(Global),
            InstructionFiles = NonEmpty(GlobalInstructionFiles),
            Scripts = ScriptsToDto(GlobalScripts),
        };
        return JsonSerializer.Serialize(blob, JsonOptions);
    }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ExportFormat = "yamca.settings";
    private const int ExportVersion = 1;

    public string ExportGlobal(bool includeApiKey)
    {
        var envelope = new SettingsExportEnvelope
        {
            Format = ExportFormat,
            Version = ExportVersion,
            Tier = "global",
            ExportedAt = DateTimeOffset.UtcNow,
            Global = new GlobalBlob
            {
                Endpoints = EndpointsToDto(Endpoints, includeApiKeys: includeApiKey),
                DefaultEndpointId = Endpoints.DefaultId,
                SystemPrompt = SystemPrompt,
                MarkdownEnabled = MarkdownEnabled,
                ReasoningDisplay = ReasoningDisplay,
                Tools = MapToDto(Global),
                InstructionFiles = NonEmpty(GlobalInstructionFiles),
                Scripts = ScriptsToDto(GlobalScripts),
            },
        };
        return JsonSerializer.Serialize(envelope, ExportJsonOptions);
    }

    public sealed record ImportResult(bool Success, string? Error)
    {
        public static ImportResult Ok() => new(true, null);
        public static ImportResult Fail(string error) => new(false, error);
    }

    public ImportResult ImportGlobal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ImportResult.Fail("File is empty.");

        SettingsExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SettingsExportEnvelope>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ImportResult.Fail($"Not valid JSON: {ex.Message}");
        }

        if (envelope is null)
            return ImportResult.Fail("File is empty or not a JSON object.");
        if (!string.Equals(envelope.Format, ExportFormat, StringComparison.Ordinal))
            return ImportResult.Fail($"Unrecognized file format (expected \"{ExportFormat}\").");
        if (envelope.Version != ExportVersion)
            return ImportResult.Fail($"Unsupported export version {envelope.Version} (this build expects {ExportVersion}).");
        if (envelope.Global is null)
            return ImportResult.Fail("File is missing the \"global\" section.");

        ApplyGlobalBlob(envelope.Global);
        Changed?.Invoke(SettingsTier.Global);
        return ImportResult.Ok();
    }

    private void ApplyGlobalBlob(GlobalBlob blob)
    {
        Endpoints = EndpointsFromBlob(blob);
        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        MarkdownEnabled = blob.MarkdownEnabled ?? true;
        ReasoningDisplay = blob.ReasoningDisplay ?? ReasoningDisplay.Collapsed;
        Global = MapFromDto(blob.Tools);
        GlobalInstructionFiles = blob.InstructionFiles?.ToArray() ?? Array.Empty<string>();
        GlobalScripts = ScriptsFromDto(blob.Scripts);
    }

    public string SerializeProject()
    {
        var blob = new ProjectBlob
        {
            Tools = MapToDto(Project),
            InstructionFiles = NonEmpty(ProjectInstructionFiles),
            InheritsGlobalInstructions = ProjectInheritsGlobalInstructions ? null : false,
            Scripts = ScriptsToDto(ProjectScripts),
        };
        return JsonSerializer.Serialize(blob, JsonOptions);
    }

    private static List<string>? NonEmpty(IReadOnlyList<string> source)
    {
        var trimmed = source.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return trimmed.Count == 0 ? null : trimmed;
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static ToolSettingsMap MapFromDto(Dictionary<string, ToolEntryDto>? dto)
    {
        if (dto is null) return ToolSettingsMap.Empty;
        var dict = new Dictionary<string, ToolPermissionSettings>(StringComparer.Ordinal);
        foreach (var (name, entry) in dto)
        {
            dict[name] = new ToolPermissionSettings
            {
                Permission = entry.Permission,
                RestrictToWorkspace = entry.RestrictToWorkspace,
            };
        }
        return new ToolSettingsMap(dict);
    }

    private static Dictionary<string, ToolEntryDto> MapToDto(ToolSettingsMap map)
    {
        var dto = new Dictionary<string, ToolEntryDto>(StringComparer.Ordinal);
        foreach (var (name, entry) in map.Entries)
        {
            dto[name] = new ToolEntryDto
            {
                Permission = entry.Permission,
                RestrictToWorkspace = entry.RestrictToWorkspace,
            };
        }
        return dto;
    }

    // --- DTOs --------------------------------------------------------------------

    private sealed class GlobalBlob
    {
        public List<EndpointDto>? Endpoints { get; set; }
        public Guid? DefaultEndpointId { get; set; }
        public string? SystemPrompt { get; set; }
        public bool? MarkdownEnabled { get; set; }
        public ReasoningDisplay? ReasoningDisplay { get; set; }
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
        public ScriptsDto? Scripts { get; set; }
    }

    private sealed class ProjectBlob
    {
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
        public bool? InheritsGlobalInstructions { get; set; }
        public ScriptsDto? Scripts { get; set; }
    }

    private sealed class EndpointDto
    {
        public Guid? Id { get; set; }
        public string? Name { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }

    private static EndpointsSettings EndpointsFromBlob(GlobalBlob blob)
    {
        if (blob.Endpoints is not { Count: > 0 } list)
            return EndpointsSettings.CreateDefault();

        var items = new List<EndpointSettings>(list.Count);
        foreach (var dto in list)
        {
            items.Add(new EndpointSettings(
                Id: dto.Id ?? Guid.NewGuid(),
                Name: string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim(),
                BaseUrl: dto.BaseUrl ?? "",
                ApiKey: dto.ApiKey ?? "",
                Model: dto.Model ?? ""));
        }

        var defaultId = blob.DefaultEndpointId is Guid id && items.Any(e => e.Id == id)
            ? id
            : items[0].Id;
        return new EndpointsSettings(items, defaultId);
    }

    private static List<EndpointDto> EndpointsToDto(EndpointsSettings endpoints, bool includeApiKeys) =>
        endpoints.Items.Select(e => new EndpointDto
        {
            Id = e.Id,
            Name = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
            BaseUrl = e.BaseUrl,
            ApiKey = includeApiKeys ? e.ApiKey : string.Empty,
            Model = e.Model,
        }).ToList();

    private sealed class SettingsExportEnvelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Tier { get; set; }
        public DateTimeOffset? ExportedAt { get; set; }
        public GlobalBlob? Global { get; set; }
    }

    private sealed class ToolEntryDto
    {
        public Yamca.Agent.Permissions.PermissionLevel? Permission { get; set; }
        public bool? RestrictToWorkspace { get; set; }
    }

    private sealed class ScriptsDto
    {
        public List<ScriptEntryDto>? Registered { get; set; }
        public List<ScriptEntryDto>? Directories { get; set; }
    }

    private sealed class ScriptEntryDto
    {
        public string? Path { get; set; }
        public string? Description { get; set; }
    }

    private static ScriptRegistry ScriptsFromDto(ScriptsDto? dto)
    {
        if (dto is null) return ScriptRegistry.Empty;

        var files = (dto.Registered ?? new List<ScriptEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new RegisteredScript(NormalizePath(e.Path!), Trim(e.Description)))
            .ToList();
        var dirs = (dto.Directories ?? new List<ScriptEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new RegisteredScriptDirectory(NormalizePath(e.Path!), Trim(e.Description)))
            .ToList();

        if (files.Count == 0 && dirs.Count == 0) return ScriptRegistry.Empty;
        return new ScriptRegistry(files, dirs);
    }

    private static ScriptsDto? ScriptsToDto(ScriptRegistry registry)
    {
        if (registry.IsEmpty) return null;
        return new ScriptsDto
        {
            Registered = registry.Registered.Count == 0
                ? null
                : registry.Registered.Select(e => new ScriptEntryDto { Path = e.Path, Description = e.Description }).ToList(),
            Directories = registry.Directories.Count == 0
                ? null
                : registry.Directories.Select(d => new ScriptEntryDto { Path = d.Path, Description = d.Description }).ToList(),
        };
    }

    private static string NormalizePath(string raw) => raw.Trim().Replace('\\', '/');
    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
