using System.Collections.Concurrent;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Caches the rendered <c>list_symbols</c> output for a single file, keyed by absolute
/// path. An entry is invalidated when the file's mtime or size changes. We cache the
/// finished string (not the live tree-sitter <c>Tree</c>) so we don't have to think
/// about native-memory lifetime across requests.
/// </summary>
public sealed class ParsedTreeCache
{
    private const int SoftCap = 1024;
    private const int EvictBatch = 256;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required DateTime MtimeUtc { get; init; }
        public required long Size { get; init; }
        public required string Rendered { get; init; }
        public long LastAccessTicks;
    }

    /// <summary>
    /// Look up a cached rendering for <paramref name="absolutePath"/>. Returns
    /// <see langword="true"/> if a fresh entry was found; the entry is touched so it
    /// outranks colder neighbors for the next eviction pass.
    /// </summary>
    public bool TryGet(string absolutePath, DateTime mtimeUtc, long size, out string rendered)
    {
        if (_entries.TryGetValue(absolutePath, out var entry) &&
            entry.MtimeUtc == mtimeUtc && entry.Size == size)
        {
            Interlocked.Exchange(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);
            rendered = entry.Rendered;
            return true;
        }
        rendered = string.Empty;
        return false;
    }

    public void Set(string absolutePath, DateTime mtimeUtc, long size, string rendered)
    {
        var entry = new Entry
        {
            MtimeUtc = mtimeUtc,
            Size = size,
            Rendered = rendered,
            LastAccessTicks = DateTime.UtcNow.Ticks,
        };
        _entries[absolutePath] = entry;

        if (_entries.Count > SoftCap)
            Evict();
    }

    /// <summary>Diagnostic — current entry count.</summary>
    public int Count => _entries.Count;

    private void Evict()
    {
        var snapshot = _entries.ToArray();
        if (snapshot.Length <= SoftCap) return;

        var toRemove = snapshot
            .OrderBy(kv => kv.Value.LastAccessTicks)
            .Take(EvictBatch)
            .Select(kv => kv.Key);

        foreach (var key in toRemove)
            _entries.TryRemove(key, out _);
    }
}
