using System.Collections.Concurrent;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Remembers tile cache keys that have no content, each for a fixed time, under a soft size cap.
/// Thread-safe.
/// </summary>
/// <remarks>
/// Eviction works on <see cref="ConcurrentDictionary{TKey,TValue}.ToArray"/>, which copies the
/// entries under all of the dictionary's locks. Running LINQ (OrderBy, ToList, ...) directly on
/// the live dictionary is not safe: LINQ sizes its buffer from <c>Count</c> and then calls
/// <c>CopyTo</c>, two separate lock acquisitions. An add in between overflows the buffer
/// (ArgumentException), and a remove in between leaves default entries whose null keys then
/// reach <c>TryRemove</c> (ArgumentNullException). Both happened in production on every busy
/// day, inside tile generation.
/// </remarks>
public sealed class NegativeTileCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _timeToLive;
    private readonly TimeProvider _time;
    private int _evicting;

    public NegativeTileCache(int capacity, TimeSpan timeToLive, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);
        _capacity = capacity;
        _timeToLive = timeToLive;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of entries, including expired ones that have not been evicted yet.</summary>
    public int Count => _expiries.Count;

    /// <summary>True when the key was recorded as missing and has not expired.</summary>
    public bool Contains(string key)
    {
        if (!_expiries.TryGetValue(key, out var expiry))
        {
            return false;
        }

        if (_time.GetUtcNow() < expiry)
        {
            return true;
        }

        // Expired. Remove it only if nobody re-added it in the meantime.
        _expiries.TryRemove(KeyValuePair.Create(key, expiry));
        return false;
    }

    /// <summary>Records the key as missing for the configured time, evicting if over capacity.</summary>
    public void Add(string key)
    {
        _expiries[key] = _time.GetUtcNow() + _timeToLive;

        if (_expiries.Count > _capacity)
        {
            Evict();
        }
    }

    public bool Remove(string key) => _expiries.TryRemove(key, out _);

    public void RemoveByPrefix(string prefix)
    {
        // Keys is a snapshot taken under all locks, so removing while looping is safe.
        foreach (var key in _expiries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _expiries.TryRemove(key, out _);
            }
        }
    }

    private void Evict()
    {
        // One evictor at a time; concurrent adders skip instead of all sorting the same entries.
        // They keep adding meanwhile, so the cache can run slightly over capacity for the length
        // of one eviction. That is what makes the cap soft.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            var live = new List<KeyValuePair<string, DateTimeOffset>>(_capacity + 1);

            foreach (var entry in _expiries.ToArray())
            {
                if (entry.Value <= now)
                {
                    // Value-matched removal: an entry re-added since the snapshot stays.
                    _expiries.TryRemove(entry);
                }
                else
                {
                    live.Add(entry);
                }
            }

            // Trim to 90% so the adds that follow don't trigger another eviction straight away.
            var excess = live.Count - _capacity * 9 / 10;
            if (excess <= 0)
            {
                return;
            }

            // Every entry lives the same time, so the soonest to expire is the oldest.
            live.Sort((a, b) => a.Value.CompareTo(b.Value));
            for (var i = 0; i < excess; i++)
            {
                _expiries.TryRemove(live[i]);
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }
}
