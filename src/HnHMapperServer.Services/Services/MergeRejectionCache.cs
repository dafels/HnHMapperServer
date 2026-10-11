using System.Collections.Concurrent;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Remembers the merge preflight's double-claim rejections (GridService) for a few minutes. A
/// rejection changes nothing in the database, so every gridUpdate of every client walking the
/// area would otherwise repeat it — each time loading the whole source map plus the target grids
/// in its bounding box, and logging the full matrix. While a verdict is remembered the same merge
/// (tenant, target map, every source map with its shift) is refused without those queries and
/// without a warning; the first recomputation after <see cref="Ttl"/> logs one warning carrying
/// the number of identical rejections it stood for. A repair (region wipe) is therefore seen at
/// the latest after <see cref="Ttl"/>. Process singleton.
/// </summary>
public sealed class MergeRejectionCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Expired entries are swept once the dictionary grows past this.
    private const int SweepThreshold = 1000;

    private sealed class Entry
    {
        public required DateTime ExpiresUtc { get; init; }
        public int Repeats;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Cache key of one merge: the target and each source with its shift onto the target.</summary>
    public static string KeyOf(string tenantId, int targetMapId, IEnumerable<(int SourceMapId, int ShiftX, int ShiftY)> sources) =>
        $"{tenantId}|{targetMapId}|" + string.Join(";", sources
            .OrderBy(s => s.SourceMapId)
            .Select(s => $"{s.SourceMapId}:{s.ShiftX},{s.ShiftY}"));

    /// <summary>True (and counted as a repeat) while a rejection of this merge is remembered.</summary>
    public bool IsRejected(string key, DateTime nowUtc)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresUtc <= nowUtc)
        {
            return false;
        }

        Interlocked.Increment(ref entry.Repeats);
        return true;
    }

    /// <summary>
    /// Remembers a fresh rejection. Returns how many repeats the previous verdict for this merge
    /// absorbed (0 for the first), for the warning.
    /// </summary>
    public int Remember(string key, DateTime nowUtc)
    {
        var fresh = new Entry { ExpiresUtc = nowUtc + Ttl };
        var previous = _entries.TryGetValue(key, out var old) ? Volatile.Read(ref old.Repeats) : 0;
        _entries[key] = fresh;

        if (_entries.Count > SweepThreshold)
        {
            // Snapshot first: never enumerate a ConcurrentDictionary other threads mutate.
            foreach (var (staleKey, staleEntry) in _entries.ToArray())
            {
                if (staleEntry.ExpiresUtc <= nowUtc)
                {
                    _entries.TryRemove(new KeyValuePair<string, Entry>(staleKey, staleEntry));
                }
            }
        }

        return previous;
    }

    /// <summary>The merge passed the preflight: drop any expired verdict for it.</summary>
    public void Forget(string key) => _entries.TryRemove(key, out _);
}
