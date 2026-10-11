using System.Collections.Concurrent;
using HnHMapperServer.Core.Enums;

namespace HnHMapperServer.Services.Services;

/// <summary>Configuration section "ClientTokenRejections".</summary>
public sealed record ClientTokenRejectionOptions
{
    /// <summary>How often a still-rejected token gets a "still rejected" Warning (decided on its next request).</summary>
    public TimeSpan SummaryInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>A token unseen this long is forgotten; its next request is a first sighting again.</summary>
    public TimeSpan IdleExpiry { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Distinct rejected tokens tracked per tenant; further ones share one overflow entry.</summary>
    public int PerTenantCap { get; init; } = 16;
}

public enum RejectionLogAction
{
    /// <summary>Counted; log at Debug only.</summary>
    None,
    /// <summary>First sighting (or first after <see cref="ClientTokenRejectionOptions.IdleExpiry"/>): one Warning.</summary>
    First,
    /// <summary>A summary interval has passed: one Warning with the count since the last one.</summary>
    Summary
}

/// <param name="Action">What to log for this request.</param>
/// <param name="Count">First: 1. Summary: requests since <paramref name="Since"/>, this one included.</param>
/// <param name="Since">When the counted window started (the previous Warning).</param>
/// <param name="Folded">The tenant already has PerTenantCap distinct rejected tokens; this one is counted in the shared overflow entry.</param>
public readonly record struct RejectionDecision(RejectionLogAction Action, long Count, DateTimeOffset Since, bool Folded);

/// <summary>
/// Turns per-request /client token rejections into a few Warnings: a stale game client keeps
/// posting every ~8 s for hours (the clients ignore 401), and a Warning per request buried the one
/// fact worth knowing — tenant X has a client using a dead token. One Warning at the first sighting
/// of each (tenant, token hash), then one summary per <see cref="ClientTokenRejectionOptions.SummaryInterval"/>
/// while it keeps coming; exact counts live in http.server.request.duration's rejection tag.
///
/// Bounded by construction: only rejections whose tenant EXISTS and whose secret is canonical are
/// attributed (callers guarantee it), at most <see cref="ClientTokenRejectionOptions.PerTenantCap"/>
/// hashes + one overflow entry per tenant — so unauthenticated callers spraying random tenant
/// prefixes cannot grow it or plant per-tenant Warnings. Everything else is pooled per reason with
/// no tenant attached. It never receives a raw secret, and the callers never log the hash. In-memory
/// per process: a restart forgets everything (an active stale client gets one new first sighting).
/// Concurrency follows NegativeTileCache's rule: pruning works on ToArray() snapshots and removes
/// buckets with value-matched TryRemove, never LINQ over the live dictionary.
/// </summary>
public sealed class ClientTokenRejectionTracker
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly ClientTokenRejectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TenantBucket> _buckets = new(StringComparer.Ordinal);
    private readonly Entry?[] _unattributed = new Entry?[Enum.GetValues<ClientTokenRejectionReason>().Length];
    private readonly Lock _unattributedLock = new();
    private long _nextPruneTicks;

    public ClientTokenRejectionTracker(ClientTokenRejectionOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new ClientTokenRejectionOptions();
        _time = time ?? TimeProvider.System;
    }

    public ClientTokenRejectionOptions Options => _options;

    /// <summary>Tenants with at least one tracked rejected token.</summary>
    public int TrackedTenantCount => _buckets.Count;

    /// <summary>Entries tracked for a tenant, overflow included (tests and diagnostics).</summary>
    public int TrackedEntryCount(string tenantId)
    {
        if (!_buckets.TryGetValue(tenantId, out var bucket))
            return 0;
        lock (bucket.Lock)
        {
            return bucket.Entries.Count + (bucket.Overflow != null ? 1 : 0);
        }
    }

    /// <summary>
    /// A rejection attributable to a real tenant: the caller has verified the tenant exists and
    /// the secret is canonical. <paramref name="tokenHash"/> is the stored-hash form, never the secret.
    /// </summary>
    public RejectionDecision RecordAttributed(string tenantId, string tokenHash, ClientTokenRejectionReason reason)
    {
        var now = _time.GetUtcNow();
        MaybePrune(now);

        while (true)
        {
            var bucket = _buckets.GetOrAdd(tenantId, static _ => new TenantBucket());
            lock (bucket.Lock)
            {
                if (bucket.Retired)
                {
                    // Pruned between our lookup and the lock: drop it (if still mapped) and retry,
                    // so no sighting is ever recorded into a bucket nobody can reach.
                    _buckets.TryRemove(KeyValuePair.Create(tenantId, bucket));
                    continue;
                }

                if (bucket.Entries.TryGetValue(tokenHash, out var entry))
                {
                    return Decide(entry, reason, now, folded: false);
                }

                if (bucket.Entries.Count < _options.PerTenantCap)
                {
                    entry = new Entry();
                    bucket.Entries[tokenHash] = entry;
                    return Decide(entry, reason, now, folded: false, isNew: true);
                }

                var isNewOverflow = bucket.Overflow == null;
                bucket.Overflow ??= new Entry();
                return Decide(bucket.Overflow, reason, now, folded: true, isNew: isNewOverflow);
            }
        }
    }

    /// <summary>
    /// A rejection that must not name a tenant (malformed token, empty or non-canonical secret,
    /// unknown tenant prefix): pooled per reason.
    /// </summary>
    public RejectionDecision RecordUnattributed(ClientTokenRejectionReason reason)
    {
        var now = _time.GetUtcNow();
        MaybePrune(now);

        lock (_unattributedLock)
        {
            var slot = (int)reason;
            var isNew = _unattributed[slot] == null;
            var entry = _unattributed[slot] ??= new Entry();
            return Decide(entry, reason, now, folded: false, isNew);
        }
    }

    /// <summary>Caller holds the entry's lock.</summary>
    private RejectionDecision Decide(Entry entry, ClientTokenRejectionReason reason, DateTimeOffset now, bool folded, bool isNew = false)
    {
        if (isNew || entry.Reason != reason || now - entry.LastSeen > _options.IdleExpiry)
        {
            entry.Reason = reason;
            entry.LastSeen = now;
            entry.LastLogged = now;
            entry.CountSinceLog = 0;
            return new RejectionDecision(RejectionLogAction.First, 1, now, folded);
        }

        entry.LastSeen = now;
        entry.CountSinceLog++;

        if (now - entry.LastLogged >= _options.SummaryInterval)
        {
            var decision = new RejectionDecision(RejectionLogAction.Summary, entry.CountSinceLog, entry.LastLogged, folded);
            entry.LastLogged = now;
            entry.CountSinceLog = 0;
            return decision;
        }

        return new RejectionDecision(RejectionLogAction.None, entry.CountSinceLog, entry.LastLogged, folded);
    }

    /// <summary>At most once per minute (whichever request wins the exchange): forget idle tokens.</summary>
    private void MaybePrune(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextPruneTicks);
        if (now.UtcTicks < due
            || Interlocked.CompareExchange(ref _nextPruneTicks, (now + PruneEvery).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (tenantId, bucket) in _buckets.ToArray())
        {
            bool retire;
            lock (bucket.Lock)
            {
                foreach (var (hash, entry) in bucket.Entries.ToArray())
                {
                    if (now - entry.LastSeen > _options.IdleExpiry)
                    {
                        bucket.Entries.Remove(hash);
                    }
                }

                if (bucket.Overflow != null && now - bucket.Overflow.LastSeen > _options.IdleExpiry)
                {
                    bucket.Overflow = null;
                }

                retire = bucket.Entries.Count == 0 && bucket.Overflow == null;
                bucket.Retired = retire;
            }

            if (retire)
            {
                // Value-matched: never removes a bucket that replaced this one meanwhile.
                _buckets.TryRemove(KeyValuePair.Create(tenantId, bucket));
            }
        }

        lock (_unattributedLock)
        {
            for (var i = 0; i < _unattributed.Length; i++)
            {
                if (_unattributed[i] is { } entry && now - entry.LastSeen > _options.IdleExpiry)
                {
                    _unattributed[i] = null;
                }
            }
        }
    }

    private sealed class TenantBucket
    {
        public readonly Lock Lock = new();
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        public Entry? Overflow;
        public bool Retired;
    }

    private sealed class Entry
    {
        public ClientTokenRejectionReason Reason;
        public DateTimeOffset LastSeen;
        public DateTimeOffset LastLogged;
        public long CountSinceLog;
    }
}
