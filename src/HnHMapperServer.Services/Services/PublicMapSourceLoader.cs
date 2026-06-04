using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Alignment;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Loads public-map sources into the <see cref="SourceGridSet"/> shape the aligner consumes.
/// Shared by generation and analysis so both derive the SAME content-based <c>SourceKey</c> and
/// read grids identically.
/// </summary>
public static class PublicMapSourceLoader
{
    /// <summary>Content-derived source key for a tenant source — never a row id, order, or priority.</summary>
    public static string KeyFor(string tenantId, int mapId) => $"{tenantId}:{mapId}";

    /// <summary>Source key for an hmap source. The "hmap:" prefix guarantees it can never collide
    /// with a tenant key ("{tenantId}:{mapId}").</summary>
    public static string KeyForHmap(int hmapSourceId) => $"hmap:{hmapSourceId}";

    /// <summary>
    /// Pure conversion of a parsed .hmap into a <see cref="SourceGridSet"/> the aligner can ingest.
    /// Each <c>HmapGridData</c> is one 100×100 grid cell at (TileX,TileY) keyed by its content-hash
    /// grid id (<c>GridIdString</c>) — the SAME id space as tenant <c>GridDataEntity.Id</c>, so hmap
    /// and tenant sources align by shared grid ids.
    /// <para>
    /// Guard: a grid with a missing/zero <c>GridId</c> has no usable content hash; it gets a
    /// per-source-unique synthetic id so it can never falsely match another source on a sentinel id
    /// (such a grid simply doesn't contribute an alignment edge).
    /// </para>
    /// </summary>
    public static SourceGridSet BuildHmapSourceGridSet(HmapData data, int hmapSourceId)
    {
        var gridMap = new Dictionary<string, (int X, int Y)>(data.Grids.Count);
        foreach (var g in data.Grids)
        {
            var id = g.GridId != 0 ? g.GridIdString : $"_raw:{hmapSourceId}:{g.TileX}:{g.TileY}";
            gridMap[id] = (g.TileX, g.TileY);
        }
        return new SourceGridSet(KeyForHmap(hmapSourceId), gridMap);
    }

    /// <summary>
    /// Parse each linked .hmap source into a <see cref="SourceGridSet"/> (for alignment) while keeping
    /// the parsed <see cref="HmapData"/> (for rendering). Sources whose file is missing or fails to
    /// parse are skipped; the caller can compare counts to detect drops.
    /// </summary>
    public static async Task<List<HmapLoadedSource>> LoadHmapAsync(
        ApplicationDbContext db,
        IReadOnlyList<PublicMapHmapSourceEntity> hmapSources,
        string gridStorage,
        CancellationToken cancellationToken = default)
    {
        var reader = new HmapReader();
        var result = new List<HmapLoadedSource>(hmapSources.Count);

        foreach (var link in hmapSources)
        {
            var file = await db.HmapSources.FindAsync(new object?[] { link.HmapSourceId }, cancellationToken);
            if (file == null) continue;

            var path = Path.Combine(gridStorage, file.FilePath);
            if (!File.Exists(path)) continue;

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                var data = reader.Read(stream);
                var gridSet = BuildHmapSourceGridSet(data, link.HmapSourceId);
                result.Add(new HmapLoadedSource(link, file, data, gridSet));
            }
            catch
            {
                // Unreadable / corrupt .hmap — skip; generation/analysis logs the count delta.
            }
        }

        return result;
    }

    public static async Task<List<(PublicMapSourceEntity Source, SourceGridSet GridSet)>> LoadAsync(
        ApplicationDbContext db,
        IReadOnlyList<PublicMapSourceEntity> sources,
        CancellationToken cancellationToken = default)
    {
        var result = new List<(PublicMapSourceEntity, SourceGridSet)>(sources.Count);
        foreach (var s in sources)
        {
            var grids = await db.Grids
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == s.TenantId && g.Map == s.MapId)
                .Select(g => new { g.Id, g.CoordX, g.CoordY })
                .ToListAsync(cancellationToken);

            var gridMap = new Dictionary<string, (int X, int Y)>(grids.Count);
            foreach (var g in grids)
                gridMap[g.Id] = (g.CoordX, g.CoordY);

            result.Add((s, new SourceGridSet(KeyFor(s.TenantId, s.MapId), gridMap)));
        }
        return result;
    }
}

/// <summary>An hmap source loaded for generation/analysis: the public-map link, the source file
/// record, the parsed grids (for rendering), and the derived grid set (for alignment).</summary>
public sealed record HmapLoadedSource(
    PublicMapHmapSourceEntity Link,
    HmapSourceEntity File,
    HmapData Data,
    SourceGridSet GridSet);
