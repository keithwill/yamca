using System.Collections.Concurrent;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// A file-keyed cache of derived, fully-managed values, invalidated when the file's
/// mtime or size changes. We deliberately cache managed results (rendered strings,
/// extracted <see cref="Symbol"/> lists) and never the native tree-sitter <c>Tree</c>,
/// so there is no native-memory lifetime to reason about across requests.
/// </summary>
/// <typeparam name="T">The cached value type. Must be immutable / safe to share.</typeparam>
public sealed class ParsedCache<T>
{
    private const int SoftCap = 1024;
    private const int EvictBatch = 256;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required DateTime MtimeUtc { get; init; }
        public required long Size { get; init; }
        public required T Value { get; init; }
        public long LastAccessTicks;
    }

    /// <summary>
    /// Look up a cached value for <paramref name="absolutePath"/>. Returns
    /// <see langword="true"/> if a fresh entry was found; the entry is touched so it
    /// outranks colder neighbors for the next eviction pass.
    /// </summary>
    public bool TryGet(string absolutePath, DateTime mtimeUtc, long size, out T value)
    {
        if (_entries.TryGetValue(absolutePath, out var entry) &&
            entry.MtimeUtc == mtimeUtc && entry.Size == size)
        {
            Interlocked.Exchange(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(string absolutePath, DateTime mtimeUtc, long size, T value)
    {
        var entry = new Entry
        {
            MtimeUtc = mtimeUtc,
            Size = size,
            Value = value,
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
