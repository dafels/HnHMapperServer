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
    /// <summary>Content-derived source key — never a row id, order, or priority.</summary>
    public static string KeyFor(string tenantId, int mapId) => $"{tenantId}:{mapId}";

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
