using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

public enum SymbolLoadStatus
{
    /// <summary>Parsed successfully; <see cref="SymbolLoadResult.Symbols"/> is populated.</summary>
    Ok,
    /// <summary>Extension not routed to any tree-sitter grammar.</summary>
    Unsupported,
    /// <summary>Routed to a grammar, but no <see cref="ISymbolExtractor"/> is registered for it.</summary>
    NoExtractor,
    /// <summary>The file could not be read (IO / access error).</summary>
    ReadError,
    /// <summary>Skipped for a benign reason (too large, binary, parse failed). See <see cref="SymbolLoadResult.Note"/>.</summary>
    Skipped,
}

/// <summary>
/// Result of loading one file's symbols. For non-<see cref="SymbolLoadStatus.Ok"/> statuses
/// <see cref="Source"/> is empty and <see cref="Symbols"/> is an empty list.
/// </summary>
public sealed record SymbolLoadResult(
    SymbolLoadStatus Status,
    string? LanguageId,
    string Source,
    IReadOnlyList<Symbol> Symbols,
    bool HasErrors,
    string? Note)
{
    public bool IsOk => Status == SymbolLoadStatus.Ok;

    private static readonly IReadOnlyList<Symbol> None = Array.Empty<Symbol>();

    public static SymbolLoadResult Unsupported(string ext) =>
        new(SymbolLoadStatus.Unsupported, null, string.Empty, None, false, $"Unsupported file type: {ext}");
    public static SymbolLoadResult NoExtractor(string languageId) =>
        new(SymbolLoadStatus.NoExtractor, languageId, string.Empty, None, false, $"No symbol extractor registered for language '{languageId}'.");
    public static SymbolLoadResult ReadError(string? note) =>
        new(SymbolLoadStatus.ReadError, null, string.Empty, None, false, note);
    public static SymbolLoadResult SkippedNote(string? languageId, string note) =>
        new(SymbolLoadStatus.Skipped, languageId, string.Empty, None, false, note);
    public static SymbolLoadResult Ok(string languageId, string source, IReadOnlyList<Symbol> symbols, bool hasErrors) =>
        new(SymbolLoadStatus.Ok, languageId, source, symbols, hasErrors, null);
}

internal sealed record ParsedSymbols(IReadOnlyList<Symbol> Symbols, bool HasErrors);

/// <summary>
/// Shared loader behind the code_* read tools: resolves a file's grammar, skips files that
/// aren't worth parsing (too large / binary), reads the source, and extracts its symbols —
/// caching the (managed) symbol list keyed by mtime+size so repeated calls within a session
/// skip the parse. The native <c>Tree</c> never escapes this class.
/// </summary>
public sealed class SymbolService
{
    private const long MaxFileBytes = 2L * 1024 * 1024;

    private readonly ParsedCache<ParsedSymbols> _cache = new();
    private readonly Dictionary<string, ISymbolExtractor> _extractorsByLanguage;

    public SymbolService(IEnumerable<ISymbolExtractor> extractors)
    {
        _extractorsByLanguage = extractors.ToDictionary(e => e.LanguageId, StringComparer.Ordinal);
    }

    /// <summary>Diagnostic — current cache entry count.</summary>
    public int CacheCount => _cache.Count;

    public async Task<SymbolLoadResult> LoadAsync(string absolutePath, CancellationToken ct)
    {
        var languageId = LanguageRouter.GetLanguageId(absolutePath);
        if (languageId is null)
            return SymbolLoadResult.Unsupported(Path.GetExtension(absolutePath));

        if (!_extractorsByLanguage.TryGetValue(languageId, out var extractor))
            return SymbolLoadResult.NoExtractor(languageId);

        FileInfo info;
        try { info = new FileInfo(absolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SymbolLoadResult.ReadError(ex.Message);
        }

        if (info.Length > MaxFileBytes)
            return SymbolLoadResult.SkippedNote(languageId, $"# skipped: too large ({info.Length / 1024} KB)");

        if (await FileProbe.IsLikelyBinaryAsync(absolutePath, ct))
            return SymbolLoadResult.SkippedNote(languageId, "# skipped: binary file");

        string source;
        try
        {
            source = await File.ReadAllTextAsync(absolutePath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SymbolLoadResult.ReadError(ex.Message);
        }

        var mtime = info.LastWriteTimeUtc;
        if (_cache.TryGet(absolutePath, mtime, info.Length, out var cached))
            return SymbolLoadResult.Ok(languageId, source, cached.Symbols, cached.HasErrors);

        ParsedSymbols parsed;
        try
        {
            using var language = new Language(extractor.LanguageId);
            using var parser = new Parser(language);
            using var tree = parser.Parse(source);
            if (tree is null)
                return SymbolLoadResult.SkippedNote(languageId, "# parse failed");

            var symbols = extractor.Extract(tree.RootNode, source).ToList();
            parsed = new ParsedSymbols(symbols, tree.RootNode.HasError);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or ArgumentException)
        {
            // Native lib for this language failed to load, or a query failed.
            return SymbolLoadResult.SkippedNote(languageId, $"# skipped: {ex.Message}");
        }

        _cache.Set(absolutePath, mtime, info.Length, parsed);
        return SymbolLoadResult.Ok(languageId, source, parsed.Symbols, parsed.HasErrors);
    }
}
