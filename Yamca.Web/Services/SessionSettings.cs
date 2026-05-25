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
    public EndpointSettings Endpoint { get; private set; } = EndpointSettings.Default;
    public string SystemPrompt { get; private set; } = DefaultSystemPrompt;
    public bool MarkdownEnabled { get; private set; } = true;
    public ReasoningDisplay ReasoningDisplay { get; private set; } = ReasoningDisplay.Collapsed;

    public IReadOnlyList<string> GlobalInstructionFiles { get; private set; } = DefaultGlobalInstructionFiles;
    public IReadOnlyList<string> ProjectInstructionFiles { get; private set; } = Array.Empty<string>();
    public bool ProjectInheritsGlobalInstructions { get; private set; } = true;

    /// <summary>Fired when the named tier has been mutated. The handler is expected
    /// to serialize that tier and write it to localStorage.</summary>
    public event Action<SettingsTier>? Changed;

    // --- mutation API (used by Settings page + IPermissionStore adapter) -----------

    public void SetEndpoint(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
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

        Endpoint = new EndpointSettings(
            BaseUrl: blob.Endpoint?.BaseUrl ?? EndpointSettings.Default.BaseUrl,
            ApiKey:  blob.Endpoint?.ApiKey  ?? EndpointSettings.Default.ApiKey,
            Model:   blob.Endpoint?.Model   ?? EndpointSettings.Default.Model);

        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        MarkdownEnabled = blob.MarkdownEnabled ?? true;
        ReasoningDisplay = blob.ReasoningDisplay ?? ReasoningDisplay.Collapsed;
        Global = firstRun ? DefaultGlobalToolSettings() : MapFromDto(blob.Tools);
        GlobalInstructionFiles = firstRun
            ? DefaultGlobalInstructionFiles
            : (blob.InstructionFiles?.ToArray() ?? Array.Empty<string>());
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
            ["read_file"]       = Workspace(PermissionLevel.Allow),
            ["write_file"]      = Workspace(PermissionLevel.Allow),
            ["delete_file"]     = Workspace(PermissionLevel.Allow),
            ["list_directory"]  = Workspace(PermissionLevel.Allow),
            ["execute_command"] = new ToolPermissionSettings { Permission = PermissionLevel.Ask },
        });
    }

    public void HydrateProject(string? json)
    {
        var blob = TryDeserialize<ProjectBlob>(json) ?? new ProjectBlob();
        Project = MapFromDto(blob.Tools);
        ProjectInstructionFiles = blob.InstructionFiles?.ToArray() ?? Array.Empty<string>();
        ProjectInheritsGlobalInstructions = blob.InheritsGlobalInstructions ?? true;
    }

    public string SerializeGlobal()
    {
        var blob = new GlobalBlob
        {
            Endpoint = new EndpointDto { BaseUrl = Endpoint.BaseUrl, ApiKey = Endpoint.ApiKey, Model = Endpoint.Model },
            SystemPrompt = SystemPrompt,
            MarkdownEnabled = MarkdownEnabled,
            ReasoningDisplay = ReasoningDisplay,
            Tools = MapToDto(Global),
            InstructionFiles = NonEmpty(GlobalInstructionFiles),
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
        var endpointDto = new EndpointDto
        {
            BaseUrl = Endpoint.BaseUrl,
            ApiKey = includeApiKey ? Endpoint.ApiKey : string.Empty,
            Model = Endpoint.Model,
        };

        var envelope = new SettingsExportEnvelope
        {
            Format = ExportFormat,
            Version = ExportVersion,
            Tier = "global",
            ExportedAt = DateTimeOffset.UtcNow,
            Global = new GlobalBlob
            {
                Endpoint = endpointDto,
                SystemPrompt = SystemPrompt,
                MarkdownEnabled = MarkdownEnabled,
                ReasoningDisplay = ReasoningDisplay,
                Tools = MapToDto(Global),
                InstructionFiles = NonEmpty(GlobalInstructionFiles),
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
        Endpoint = new EndpointSettings(
            BaseUrl: blob.Endpoint?.BaseUrl ?? EndpointSettings.Default.BaseUrl,
            ApiKey:  blob.Endpoint?.ApiKey  ?? EndpointSettings.Default.ApiKey,
            Model:   blob.Endpoint?.Model   ?? EndpointSettings.Default.Model);
        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        MarkdownEnabled = blob.MarkdownEnabled ?? true;
        ReasoningDisplay = blob.ReasoningDisplay ?? ReasoningDisplay.Collapsed;
        Global = MapFromDto(blob.Tools);
        GlobalInstructionFiles = blob.InstructionFiles?.ToArray() ?? Array.Empty<string>();
    }

    public string SerializeProject()
    {
        var blob = new ProjectBlob
        {
            Tools = MapToDto(Project),
            InstructionFiles = NonEmpty(ProjectInstructionFiles),
            InheritsGlobalInstructions = ProjectInheritsGlobalInstructions ? null : false,
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
        public EndpointDto? Endpoint { get; set; }
        public string? SystemPrompt { get; set; }
        public bool? MarkdownEnabled { get; set; }
        public ReasoningDisplay? ReasoningDisplay { get; set; }
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
    }

    private sealed class ProjectBlob
    {
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
        public bool? InheritsGlobalInstructions { get; set; }
    }

    private sealed class EndpointDto
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }

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
}
