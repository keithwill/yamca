using System.Text.Json;
using System.Text.Json.Serialization;
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
        "You are a coding assistant operating in {{workspace}}.";

    public ToolSettingsMap Project { get; private set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap Global { get; private set; } = ToolSettingsMap.Empty;
    public EndpointSettings Endpoint { get; private set; } = EndpointSettings.Default;
    public string SystemPrompt { get; private set; } = DefaultSystemPrompt;

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
        var blob = TryDeserialize<GlobalBlob>(json) ?? new GlobalBlob();

        Endpoint = new EndpointSettings(
            BaseUrl: blob.Endpoint?.BaseUrl ?? EndpointSettings.Default.BaseUrl,
            ApiKey:  blob.Endpoint?.ApiKey  ?? EndpointSettings.Default.ApiKey,
            Model:   blob.Endpoint?.Model   ?? EndpointSettings.Default.Model);

        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        Global = MapFromDto(blob.Tools);
    }

    public void HydrateProject(string? json)
    {
        var blob = TryDeserialize<ProjectBlob>(json) ?? new ProjectBlob();
        Project = MapFromDto(blob.Tools);
    }

    public string SerializeGlobal()
    {
        var blob = new GlobalBlob
        {
            Endpoint = new EndpointDto { BaseUrl = Endpoint.BaseUrl, ApiKey = Endpoint.ApiKey, Model = Endpoint.Model },
            SystemPrompt = SystemPrompt,
            Tools = MapToDto(Global),
        };
        return JsonSerializer.Serialize(blob, JsonOptions);
    }

    public string SerializeProject()
    {
        var blob = new ProjectBlob { Tools = MapToDto(Project) };
        return JsonSerializer.Serialize(blob, JsonOptions);
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
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
    }

    private sealed class ProjectBlob
    {
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
    }

    private sealed class EndpointDto
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }

    private sealed class ToolEntryDto
    {
        public Yamca.Agent.Permissions.PermissionLevel? Permission { get; set; }
        public bool? RestrictToWorkspace { get; set; }
    }
}
