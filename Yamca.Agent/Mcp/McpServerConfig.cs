using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yamca.Agent.Mcp;

/// <summary>
/// One MCP server as it appears in <c>localStorage</c>. The <see cref="Stdio"/>
/// payload mirrors the de-facto <c>mcp.json</c> shape so users can paste a
/// server's README snippet without translation.
/// </summary>
public sealed record McpServerConfig(
    string Id,
    bool Enabled,
    McpStdioConfig Stdio);

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

public enum McpConfigParseStatus
{
    Ok,
    InvalidJson,
    MissingId,
    InvalidId,
    UnsupportedTransport,
    MissingCommand,
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
/// Parses both the array-of-servers blob stored in localStorage and the
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
        foreach (var c in configs)
        {
            dtos.Add(new ServerDto
            {
                Id = c.Id,
                Enabled = c.Enabled,
                Config = ToConfigDto(c.Stdio),
            });
        }
        return JsonSerializer.Serialize(dtos, Options);
    }

    /// <summary>Parse a single server pasted from a README. Accepts both the
    /// Yamca wrapper shape (<c>{ "id": ..., "config": { ... } }</c>) and the
    /// bare <c>mcp.json</c> shape (<c>{ "command": ..., "args": ... }</c>).
    /// Callers may pass <paramref name="overrideId"/> from the dialog's id
    /// field; it wins over an id embedded in the JSON.</summary>
    public static McpConfigParseResult ParseSingle(string? json, string? overrideId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, "Config is empty.");

        ServerDto? dto;
        try { dto = JsonSerializer.Deserialize<ServerDto>(json, Options); }
        catch (JsonException ex) { return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, ex.Message); }
        if (dto is null) return McpConfigParseResult.Fail(McpConfigParseStatus.InvalidJson, "Config is empty.");

        // Bare mcp.json form: { "command": "...", "args": [...] }
        if (dto.Config is null && !string.IsNullOrWhiteSpace(dto.Command))
        {
            dto.Config = new ConfigDto
            {
                Command = dto.Command,
                Args = dto.Args,
                Env = dto.Env,
                Cwd = dto.Cwd,
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
                "Missing \"config\" — provide command/args (stdio) or wrap it as { \"id\":..., \"config\":{...} }.");

        // Phase 1 is stdio-only. URL/headers are detected and rejected so users
        // see a clear error instead of a silent disable.
        if (dto.Config.Url is not null || dto.Config.Headers is not null)
            return McpConfigParseResult.Fail(McpConfigParseStatus.UnsupportedTransport,
                "HTTP transport is not supported in this build. Use a stdio command.");

        if (string.IsNullOrWhiteSpace(dto.Config.Command))
            return McpConfigParseResult.Fail(McpConfigParseStatus.MissingCommand, "Missing \"command\".");

        var stdio = new McpStdioConfig(
            Command: dto.Config.Command!,
            Args: dto.Config.Args is null ? Array.Empty<string>() : dto.Config.Args.ToArray(),
            Env: dto.Config.Env is null
                ? null
                : new Dictionary<string, string>(dto.Config.Env, StringComparer.Ordinal),
            WorkingDirectory: string.IsNullOrWhiteSpace(dto.Config.Cwd) ? null : dto.Config.Cwd);

        var enabled = dto.Enabled ?? true;
        return McpConfigParseResult.Ok(new McpServerConfig(dto.Id!, enabled, stdio));
    }

    private static ConfigDto ToConfigDto(McpStdioConfig stdio) => new()
    {
        Command = stdio.Command,
        Args = stdio.Args.Count == 0 ? null : new List<string>(stdio.Args),
        Env = stdio.Env is null || stdio.Env.Count == 0
            ? null
            : new Dictionary<string, string>(stdio.Env, StringComparer.Ordinal),
        Cwd = stdio.WorkingDirectory,
    };

    private sealed class ServerDto
    {
        public string? Id { get; set; }
        public bool? Enabled { get; set; }
        public ConfigDto? Config { get; set; }

        // Bare-mcp.json passthroughs (the user pasted just the inner object).
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Cwd { get; set; }
    }

    private sealed class ConfigDto
    {
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Cwd { get; set; }

        // Detected only — not honored in Phase 1.
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }
}
