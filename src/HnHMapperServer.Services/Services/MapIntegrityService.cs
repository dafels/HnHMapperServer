using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Read-only cross-tenant integrity scan. Every query uses IgnoreQueryFilters() — this runs in a
/// superadmin request whose ambient tenant context is a different tenant (or none), the same
/// rationale as TenantDataPurgeService / MapRegionWipeService.
/// </summary>
public class MapIntegrityService : IMapIntegrityService
{
    /// <summary>Contested cells listed per map before the UI has to rely on the counts alone.</summary>
    public const int SampleCellCap = 12;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<MapIntegrityService> _logger;

    public MapIntegrityService(ApplicationDbContext db, ILogger<MapIntegrityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MapIntegrityReportDto> ScanAsync(CancellationToken ct = default)
    {
        var report = new MapIntegrityReportDto
        {
            ScannedAt = DateTime.UtcNow,
            TenantsScanned = await _db.Tenants.IgnoreQueryFilters().CountAsync(ct),
            TotalGrids = await _db.Grids.IgnoreQueryFilters().CountAsync(ct)
        };

        // Cells claimed by more than one grid id — the corruption fingerprint. One aggregate
        // query over the whole Grids table; grouped per (tenant, map) afterwards.
        var contestedCells = await _db.Grids.IgnoreQueryFilters()
            .GroupBy(g => new { g.TenantId, g.Map, g.CoordX, g.CoordY })
            .Where(grp => grp.Count() > 1)
            .Select(grp => new
            {
                grp.Key.TenantId,
                grp.Key.Map,
                grp.Key.CoordX,
                grp.Key.CoordY
            })
            .ToListAsync(ct);

        var placeholderRows = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.Id == "0")
            .Select(g => new { g.TenantId, g.Map, g.CoordX, g.CoordY })
            .ToListAsync(ct);

        // Display names for everything involved.
        var tenantIds = contestedCells.Select(c => c.TenantId)
            .Concat(placeholderRows.Select(p => p.TenantId))
            .Distinct()
            .ToList();
        var mapIds = contestedCells.Select(c => c.Map).Distinct().ToList();

        var tenantNames = await _db.Tenants.IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var mapNames = await _db.Maps.IgnoreQueryFilters()
            .Where(m => mapIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        foreach (var group in contestedCells
                     .GroupBy(c => (c.TenantId, c.Map))
                     .OrderByDescending(g => g.Count()))
        {
            ct.ThrowIfCancellationRequested();

            var cells = group.OrderBy(c => c.CoordX).ThenBy(c => c.CoordY).ToList();
            var issue = new MapIntegrityIssueDto
            {
                TenantId = group.Key.TenantId,
                TenantName = tenantNames.GetValueOrDefault(group.Key.TenantId, group.Key.TenantId),
                MapId = group.Key.Map,
                MapName = mapNames.GetValueOrDefault(group.Key.Map, $"map {group.Key.Map}"),
                ContestedCellCount = cells.Count,
                MinX = cells.Min(c => c.CoordX),
                MaxX = cells.Max(c => c.CoordX),
                MinY = cells.Min(c => c.CoordY),
                MaxY = cells.Max(c => c.CoordY)
            };

            // Owners for a capped sample of cells — indexed point lookups, a handful per issue.
            foreach (var cell in cells.Take(SampleCellCap))
            {
                var owners = await _db.Grids.IgnoreQueryFilters()
                    .Where(g => g.TenantId == group.Key.TenantId && g.Map == group.Key.Map
                                && g.CoordX == cell.CoordX && g.CoordY == cell.CoordY)
                    .Select(g => g.Id)
                    .ToListAsync(ct);
                issue.SampleCells.Add(new ContestedCellDto { X = cell.CoordX, Y = cell.CoordY, GridIds = owners });
            }

            report.ContestedMaps.Add(issue);
        }

        report.PlaceholderRows = placeholderRows
            .Select(p => new PlaceholderGridRowDto
            {
                TenantId = p.TenantId,
                TenantName = tenantNames.GetValueOrDefault(p.TenantId, p.TenantId),
                MapId = p.Map,
                X = p.CoordX,
                Y = p.CoordY
            })
            .ToList();

        _logger.LogInformation(
            "Map integrity scan: {Tenants} tenants, {Grids} grids, {ContestedMaps} maps with contested cells, {Placeholders} placeholder rows",
            report.TenantsScanned, report.TotalGrids, report.ContestedMaps.Count, report.PlaceholderRows.Count);

        return report;
    }
}
