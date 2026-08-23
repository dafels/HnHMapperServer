using System.Collections.Concurrent;
using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// One display row of a cookbook recipe variation with world-effective values applied:
/// the per-world snapshot when one exists for the selected world, the canonical
/// (all-worlds merge) values otherwise. Used both by the per-food variations sub-table
/// and, via <see cref="CookbookFlatCache"/>, by the flat "All recipes" view.
/// </summary>
public sealed record CookbookVariantRow(
    FoodVariantDto Variant,
    string RecipeText,
    string SearchBlob,
    decimal Total,
    decimal PerHunger,
    Dictionary<string, decimal> StatTotals,
    Dictionary<string, decimal> StatTierTotals,
    List<FoodFepDto> EffectiveFeps,
    decimal EffectiveHunger,
    int EffectiveEnergy);

/// <summary>One flat-view entry: the owning food's id plus the prebuilt variation row.</summary>
public sealed record CookbookFlatEntry(int FoodId, CookbookVariantRow Row);

/// <summary>Builds <see cref="CookbookVariantRow"/>s from catalog DTOs (pure).</summary>
public static class CookbookRows
{
    /// <summary>
    /// Builds one row. <paramref name="worldGenus"/> is the selected world's genus hash,
    /// or null for no selection / the Untagged bucket (both show canonical values).
    /// </summary>
    public static CookbookVariantRow Build(FoodVariantDto variant, string? worldGenus)
    {
        var worldValue = worldGenus != null
            ? variant.WorldValues.FirstOrDefault(w => w.Genus == worldGenus)
            : null;
        var feps = worldValue?.Feps ?? variant.Feps;
        var hunger = worldValue?.Hunger ?? variant.Hunger;
        var energy = worldValue?.Energy ?? variant.Energy;

        var text = string.Join(", ", variant.Ingredients.Select(i =>
            i.Percentage != 100 ? $"{i.Name} {i.Percentage:0.#}%" : i.Name));
        var total = feps.Sum(f => f.Value);
        var statTotals = feps
            .GroupBy(f => f.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);
        var statTierTotals = feps
            .GroupBy(f => f.Attribute.ToUpperInvariant() + f.Tier)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);
        return new CookbookVariantRow(
            variant,
            text,
            text.ToLowerInvariant(),
            total,
            hunger > 0 ? total / hunger : 0m,
            statTotals,
            statTierTotals,
            feps,
            hunger,
            energy);
    }

    /// <summary>
    /// Synthetic row for a food with no recorded variations (possible for imported
    /// catalogs), built from the food's canonical values with an empty signature so
    /// favorites/panels target the whole food.
    /// </summary>
    public static CookbookVariantRow BuildFromFood(FoodDto food, string? worldGenus) =>
        Build(new FoodVariantDto
        {
            FoodId = food.Id,
            IngredientSignature = string.Empty,
            Energy = food.Energy,
            Hunger = food.Hunger,
            TimesSeen = 1,
            Worlds = food.Worlds,
            WorldValues = food.WorldValues,
            Feps = food.Feps,
            Ingredients = food.Ingredients
        }, worldGenus);
}

/// <summary>
/// Process-wide cache of the flat "All recipes" rows, shared across Blazor circuits.
/// A full catalog is ~50k variation DTOs (~100MB as built rows), and Blazor Server
/// retains disconnected circuits for minutes — per-circuit copies would multiply that
/// by every refresh, so circuits share one row list per (tenant, selected world) and
/// keep only references. Entries refresh every few minutes (the cookbook page accepts
/// circuit-lifetime staleness everywhere else too) and idle tenants are evicted.
/// </summary>
public sealed class CookbookFlatCache
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleEviction = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many worlds' built row sets one tenant keeps at once. Each set is a full
    /// ~49k-row copy of the catalog (well over 100 MB for a large tenant), and it is
    /// derived data — rebuilding it from the cached DTOs costs about two seconds. A
    /// tenant with three worlds plus "Untagged" would otherwise hold four copies for the
    /// life of the entry just because someone clicked through the world chips.
    /// </summary>
    private const int MaxWorldsPerTenant = 2;

    private sealed class TenantEntry
    {
        public required DateTime CreatedUtc { get; init; }
        public required Task<List<FoodVariantDto>> Variants { get; init; }
        public long LastAccessTicks;
        public readonly ConcurrentDictionary<string, IReadOnlyList<CookbookFlatEntry>> RowsByWorld = new(StringComparer.Ordinal);

        /// <summary>Last-use stamp per world key, for the trim below.</summary>
        private readonly ConcurrentDictionary<string, long> _worldTicks = new(StringComparer.Ordinal);

        public void TouchWorld(string worldKey) => _worldTicks[worldKey] = DateTime.UtcNow.Ticks;

        /// <summary>Drops the least recently used world row sets, never the current one.</summary>
        public void TrimWorlds(string keepWorldKey)
        {
            while (RowsByWorld.Count > MaxWorldsPerTenant)
            {
                var victim = _worldTicks
                    .Where(kv => kv.Key != keepWorldKey && RowsByWorld.ContainsKey(kv.Key))
                    .OrderBy(kv => kv.Value)
                    .Select(kv => (string?)kv.Key)
                    .FirstOrDefault();

                if (victim is null)
                {
                    return;
                }

                RowsByWorld.TryRemove(victim, out _);
                _worldTicks.TryRemove(victim, out _);
            }
        }
    }

    private readonly ConcurrentDictionary<string, Lazy<TenantEntry>> _tenants = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the shared flat rows for one tenant and world selection, fetching the
    /// bulk variation list via <paramref name="fetch"/> on a cache miss. The caller
    /// supplies the fetch because the Web→API call must carry the requesting user's
    /// auth cookie (scoped handler); the result is tenant-scoped data identical for
    /// every user of the tenant, so sharing it is safe. Throws when the fetch fails
    /// (the failed entry is dropped so the next call retries).
    /// </summary>
    public async Task<IReadOnlyList<CookbookFlatEntry>> GetRowsAsync(
        string tenantId, string? worldGenus, Func<Task<List<FoodVariantDto>>> fetch)
    {
        EvictIdle(tenantId);

        var entry = GetOrRefreshEntry(tenantId, fetch);
        Volatile.Write(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);

        List<FoodVariantDto> variants;
        try
        {
            variants = await entry.Variants;
        }
        catch
        {
            // Drop only if the stored entry is still this failed one.
            if (_tenants.TryGetValue(tenantId, out var current)
                && current.IsValueCreated && ReferenceEquals(current.Value, entry))
            {
                _tenants.TryRemove(tenantId, out _);
            }

            throw;
        }

        var worldKey = worldGenus ?? string.Empty;
        var rows = entry.RowsByWorld.GetOrAdd(worldKey, _ =>
            variants.Select(v => new CookbookFlatEntry(v.FoodId, CookbookRows.Build(v, worldGenus))).ToList());

        entry.TouchWorld(worldKey);
        entry.TrimWorlds(worldKey);
        return rows;
    }

    /// <summary>Drops one tenant's cached data (used by the page's retry path).</summary>
    public void Invalidate(string tenantId) => _tenants.TryRemove(tenantId, out _);

    private TenantEntry GetOrRefreshEntry(string tenantId, Func<Task<List<FoodVariantDto>>> fetch)
    {
        while (true)
        {
            var lazy = _tenants.GetOrAdd(tenantId, _ => NewEntry(fetch));
            var entry = lazy.Value;

            // Refresh only after a completed fetch has aged out — an in-flight fetch is
            // never replaced, so concurrent circuits share one request.
            if (!entry.Variants.IsCompleted || DateTime.UtcNow - entry.CreatedUtc < RefreshInterval)
            {
                return entry;
            }

            if (_tenants.TryUpdate(tenantId, NewEntry(fetch), lazy))
            {
                continue; // re-read the freshly stored entry
            }
        }
    }

    private static Lazy<TenantEntry> NewEntry(Func<Task<List<FoodVariantDto>>> fetch) =>
        new(() => new TenantEntry
        {
            CreatedUtc = DateTime.UtcNow,
            Variants = fetch(),
            LastAccessTicks = DateTime.UtcNow.Ticks
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    private void EvictIdle(string exceptTenantId)
    {
        var cutoff = DateTime.UtcNow - IdleEviction;
        foreach (var (key, lazy) in _tenants)
        {
            if (key == exceptTenantId || !lazy.IsValueCreated)
            {
                continue;
            }

            if (new DateTime(Volatile.Read(ref lazy.Value.LastAccessTicks), DateTimeKind.Utc) < cutoff)
            {
                _tenants.TryRemove(key, out _);
            }
        }
    }
}
