using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Mcp;

/// <summary>
/// One MCP server as it appears in <c>mcp.json</c>. Exactly one of
/// <see cref="Stdio"/> or <see cref="Http"/> is populated; the parser enforces
/// that and the connection layer dispatches on whichever is set.
/// </summary>
public sealed record McpServerConfig(
    string Id,
    bool Enabled,
    McpStdioConfig? Stdio,
    McpHttpConfig? Http = null,
    int? CallTimeoutSeconds = null,
    Availability DefaultToolAvailability = Availability.Deferred)
{
    public McpTransportKind TransportKind =>
        Http is not null ? McpTransportKind.Http : McpTransportKind.Stdio;
}

public enum McpTransportKind
{
    Stdio,
    Http,
}

/// <summary>
/// stdio-transport config. The fields line up 1:1 with the well-known
/// <c>command</c>/<c>args</c>/<c>env</c>/<c>cwd</c> entries used by Claude
/// Desktop, Cursor, Continue, and Zed.
/// </summary>
public sealed record McpStdioConfig(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null);

/// <summary>
/// HTTP-transport config (Streamable HTTP / SSE — the SDK auto-detects).
/// Mirrors the <c>url</c>/<c>headers</c> shape pasted from server READMEs.
/// </summary>
public sealed record McpHttpConfig(
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null);

public enum McpConfigParseStatus
{
    Ok,
    InvalidJson,
    MissingId,
    InvalidId,
    UnsupportedTransport,
    MissingCommand,
    InvalidUrl,
    InvalidTimeout,
}

public sealed record McpConfigParseResult(
    McpConfigParseStatus Status,
    McpServerConfig? Config,
    string? Error)
{
    public static McpConfigParseResult Ok(McpServerConfig config) =>
        new(McpConfigParseStatus.Ok, config, null);

    public static McpConfigParseResult Fail(McpConfigParseStatus status, string error) =>
        new(status, null, error);
}

/// <summary>
/// Parses both the array-of-servers blob stored in mcp.json and the
/// single-server "paste from README" form used by the add-server dialog.
/// </summary>
public static class McpServerConfigJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The servers seeded into a fresh <c>mcp.json</c> on first run: the two
    /// browser-automation MCPs, shipped <b>disabled</b>. They're the common ones users reach
    /// for, and pre-listing them (rather than making the user hand-write the JSON) means
    /// enabling is a one-click toggle. Both launch via <c>npx</c>, so the only host requirement
    /// is Node.js on PATH; <c>chrome-devtools</c> drives an existing Chrome, while
    /// <c>playwright</c> is pinned to <c>--channel chrome</c> so it reuses installed Chrome
    /// instead of triggering a multi-hundred-MB browser download on first enable.
    /// Tools stay <see cref="Availability.Deferred"/> — each server exposes ~30 tools, so they
    /// belong behind <c>lookup_tool</c> rather than in every turn's prompt prefix.</summary>
    public static IReadOnlyList<McpServerConfig> DefaultConfigs() => new[]
    {
        new McpServerConfig(
            Id: "chrome-devtools",
            Enabled: false,
            Stdio: new McpStdioConfig("npx", new[] { "-y", "chrome-devtools-mcp@latest" }),
            Http: null,
            CallTimeoutSeconds: null,
            DefaultToolAvailability: Availability.Deferred),
        new McpServerConfig(
            Id: "playwright",
            Enabled: false,
            Stdio: new McpStdioConfig("npx", new[] { "-y", "@playwright/mcp@latest", "--channel", "chrome" }),
            Http: null,
            CallTimeoutSeconds: null,
            DefaultToolAvailability: Availability.Deferred),
    };

    /// <summary>Identifier rules: kebab/snake-case slug, ASCII letters/digits/underscore/hyphen,
    /// 1–48 chars. The id is embedded in tool names — keep it predictable.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 48) return false;
        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    public static IReadOnlyList<McpServerConfig> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<McpServerConfig>();
        List<ServerDto>? dtos;
        try { dtos = JsonSerializer.Deserialize<List<ServerDto>>(json, Options); }
        catch (JsonException) { return Array.Empty<McpServerConfig>(); }
        if (dtos is null) return Array.Empty<McpServerConfig>();

        var result = new List<McpServerConfig>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in dtos)
        {
            var parsed = FromDto(dto);
            if (parsed.Config is null) continue;
            if (!seen.Add(parsed.Config.Id)) continue;
            result.Add(parsed.Config);
        }
        return result;
    }

    public static string SerializeList(IEnumerable<McpServerConfig> configs)
    {
        var dtos = new List<ServerDto>();
        foreach (var c in configs) dtos.Add(ToDto(c));
        return JsonSerializer.Serialize(dtos, Options);
    }

    /// <summary>Render one config back to the wrapped JSON shape the add/edit
    /// dialog accepts. Pretty-printed so the user can read what they're about
    /// to edit.</summary>
    public static string SerializeSingle(McpServerConfig config)
    {
        return JsonSerializer.Serialize(ToDto(config), PrettyOptions);
    }

    /// <summary>Parse a single server pasted from a README. Accepts both the
    /// Yamca wrapper shape (<c>{ "id": ..., "config": { ... } }</c>) and the
    /// bare <c>mcp.json</c> shape (<c>{ "command": ..., "args": ... }</c> or
    /// <c>{ "url": ... }</c>). Callers may pass <paramref name="overrideId"/>
    /// from the dialog's id field; it wins over an id embedded in the JSON.</summary>
    public static McpConfigParseResult ParseSingle(string? json, string? overrideId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, "Config is empty.");

        ServerDto? dto;
        try { dto = JsonSerializer.Deserialize<ServerDto>(json, Options); }
        catch (JsonException ex) { return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, ex.Message); }
        if (dto is null) return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, "Config is empty.");

        // Bare mcp.json forms — promote the top-level fields into a synthesized config block.
        if (dto.Config is null && (!string.IsNullOrWhiteSpace(dto.Command) || !string.IsNullOrWhiteSpace(dto.Url)))
        {
            dto.Config = new ConfigDto
            {
                Command = dto.Command,
                Args = dto.Args,
                Env = dto.Env,
                Cwd = dto.Cwd,
                Url = dto.Url,
                Headers = dto.Headers,
            };
        }

        if (!string.IsNullOrWhiteSpace(overrideId)) dto.Id = overrideId;
        return FromDto(dto);
    }

    private static McpConfigParseResult FromDto(ServerDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            return McpConfigParseResult.Fail(McpConfigParseStatus.MissingId, "Missing \"id\".");
        if (!IsValidId(dto.Id))
            return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidId,
                $"Id \"{dto.Id}\" is not a valid slug. Use ASCII letters, digits, hyphens, or underscores (≤48 chars).");

        if (dto.Config is null)
            return McpConfigParseResult.Fail(McpConfigParseStatus.UnsupportedTransport,
                "Missing \"config\" — provide command/args (stdio) or url/headers (http).");

        var hasStdio = !string.IsNullOrWhiteSpace(dto.Config.Command);
        var hasHttp = !string.IsNullOrWhiteSpace(dto.Config.Url);

        if (hasStdio && hasHttp)
            return McpConfigParseResult.Fail(McpConfigParseStatus.UnsupportedTransport,
                "Set either \"command\" (stdio) or \"url\" (http), not both.");

        if (!hasStdio && !hasHttp)
            return McpConfigParseResult.Fail(McpConfigParseStatus.MissingCommand,
                "Missing \"command\" (stdio) or \"url\" (http).");

        int? timeoutSeconds = null;
        if (dto.Config.TimeoutSeconds is { } t)
        {
            if (t <= 0 || t > 3600)
                return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidTimeout,
                    "\"timeoutSeconds\" must be between 1 and 3600.");
            timeoutSeconds = t;
        }

        var enabled = dto.Enabled ?? true;
        var defaultAvailability = dto.DefaultToolAvailability ?? Availability.Deferred;

        if (hasStdio)
        {
            var stdio = new McpStdioConfig(
                Command: dto.Config.Command!,
                Args: dto.Config.Args is null ? Array.Empty<string>() : dto.Config.Args.ToArray(),
                Env: dto.Config.Env is null
                    ? null
                    : new Dictionary<string, string>(dto.Config.Env, StringComparer.Ordinal),
                WorkingDirectory: string.IsNullOrWhiteSpace(dto.Config.Cwd) ? null : dto.Config.Cwd);
            return McpConfigParseResult.Ok(new McpServerConfig(
                dto.Id!, enabled, stdio, Http: null,
                CallTimeoutSeconds: timeoutSeconds,
                DefaultToolAvailability: defaultAvailability));
        }

        // HTTP — validate the URL eagerly so a typo surfaces in the dialog
        // instead of as a connect-time HttpRequestException later.
        if (!Uri.TryCreate(dto.Config.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidUrl,
                "\"url\" must be an absolute http:// or https:// URL.");
        }

        var http = new McpHttpConfig(
            Url: uri.ToString(),
            Headers: dto.Config.Headers is null
                ? null
                : new Dictionary<string, string>(dto.Config.Headers, StringComparer.Ordinal));
        return McpConfigParseResult.Ok(new McpServerConfig(
            dto.Id!, enabled, Stdio: null, Http: http,
            CallTimeoutSeconds: timeoutSeconds,
            DefaultToolAvailability: defaultAvailability));
    }

    private static ServerDto ToDto(McpServerConfig c) => new()
    {
        Id = c.Id,
        Enabled = c.Enabled,
        DefaultToolAvailability = c.DefaultToolAvailability,
        Config = new ConfigDto
        {
            Command = c.Stdio?.Command,
            Args = c.Stdio is null || c.Stdio.Args.Count == 0 ? null : new List<string>(c.Stdio.Args),
            Env = c.Stdio?.Env is null || c.Stdio.Env.Count == 0
                ? null
                : new Dictionary<string, string>(c.Stdio.Env, StringComparer.Ordinal),
            Cwd = c.Stdio?.WorkingDirectory,
            Url = c.Http?.Url,
            Headers = c.Http?.Headers is null || c.Http.Headers.Count == 0
                ? null
                : new Dictionary<string, string>(c.Http.Headers, StringComparer.Ordinal),
            TimeoutSeconds = c.CallTimeoutSeconds,
        },
    };

    private sealed class ServerDto
    {
        public string? Id { get; set; }
        public bool? Enabled { get; set; }
        public Availability? DefaultToolAvailability { get; set; }
        public ConfigDto? Config { get; set; }

        // Bare-mcp.json passthroughs (the user pasted just the inner object).
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Cwd { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }

    private sealed class ConfigDto
    {
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Cwd { get; set; }

        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }

        public int? TimeoutSeconds { get; set; }
    }
}
