using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Alignment;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Read-only pre-merge analysis: runs the same <see cref="IAlignmentSolver"/> the generator uses,
/// without rendering, and persists a single report row per public map. See
/// <see cref="IPublicMapAnalysisService"/>.
/// </summary>
public class PublicMapAnalysisService : IPublicMapAnalysisService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ApplicationDbContext _db;
    private readonly IAlignmentSolver _solver;
    private readonly string _gridStorage;
    private readonly ILogger<PublicMapAnalysisService> _logger;

    public PublicMapAnalysisService(
        ApplicationDbContext db,
        IAlignmentSolver solver,
        IConfiguration configuration,
        ILogger<PublicMapAnalysisService> logger)
    {
        _db = db;
        _solver = solver;
        _gridStorage = configuration["GridStorage"] ?? "map";
        _logger = logger;
    }

    public async Task<PublicMapAnalysisReportDto?> AnalyzeAsync(string publicMapId, CancellationToken cancellationToken = default)
    {
        var publicMap = await _db.PublicMaps.FirstOrDefaultAsync(p => p.Id == publicMapId, cancellationToken);
        if (publicMap == null) return null;

        // Load BOTH source types and align them together (hmap + tenant share a grid-id space).
        var tenantSources = await _db.PublicMapSources
            .Where(s => s.PublicMapId == publicMapId)
            .ToListAsync(cancellationToken);
        var hmapLinks = await _db.PublicMapHmapSources
            .Where(h => h.PublicMapId == publicMapId)
            .ToListAsync(cancellationToken);

        var tenantLoaded = await PublicMapSourceLoader.LoadAsync(_db, tenantSources, cancellationToken);
        var hmapLoaded = await PublicMapSourceLoader.LoadHmapAsync(_db, hmapLinks, _gridStorage, cancellationToken);
        var metas = await BuildSourceMetasAsync(tenantLoaded, hmapLoaded, cancellationToken);

        var metaByKey = metas.ToDictionary(m => m.GridSet.SourceKey, m => m);
        var labelByKey = metas.ToDictionary(m => m.GridSet.SourceKey, m => m.DisplayLabel);
        var allSets = metas.Select(m => m.GridSet).ToList();

        var result = _solver.Align(allSets);

        var (minX, maxX, minY, maxY, zoom0, totalTiles) = EstimateBoundsAndTiles(allSets, result.Offsets);

        var report = new PublicMapAnalysisReportDto
        {
            PublicMapId = publicMapId,
            AnalyzedAt = DateTime.UtcNow,
            AlignmentHash = ComputeAlignmentHash(allSets),
            TotalSources = metas.Count,
            TotalGrids = allSets.Sum(s => s.Grids.Count),
            ClusterCount = result.Clusters.Count,
            StandaloneCount = result.Clusters.Count(c => c.IsStandalone),
            WarningCount = result.Warnings.Count,
            EstMinX = minX,
            EstMaxX = maxX,
            EstMinY = minY,
            EstMaxY = maxY,
            EstZoom0TileCount = zoom0,
            EstTotalTileCount = totalTiles,
        };

        foreach (var cluster in result.Clusters)
        {
            var dto = new AlignmentClusterDto
            {
                Index = cluster.Index,
                IsStandalone = cluster.IsStandalone,
                GridCount = cluster.GridCount,
                OriginX = cluster.PlacedOriginX,
                OriginY = cluster.PlacedOriginY,
                Width = cluster.LocalWidth,
                Height = cluster.LocalHeight,
                Confidence = cluster.Confidence,
                MaxResidual = cluster.MaxResidual,
            };
            foreach (var key in cluster.SourceKeys)
            {
                if (!metaByKey.TryGetValue(key, out var m)) continue;
                var off = result.Offsets[key];
                dto.Sources.Add(new AlignmentSourceRefDto
                {
                    SourceType = m.SourceType,
                    TenantId = m.TenantId,
                    TenantName = m.TenantName,
                    MapId = m.MapId,
                    MapName = m.MapName,
                    HmapSourceId = m.HmapSourceId,
                    GridCount = m.GridSet.Grids.Count,
                    OffsetX = off.X,
                    OffsetY = off.Y,
                });
            }
            report.Clusters.Add(dto);
        }

        foreach (var e in result.Edges)
        {
            report.Pairs.Add(new AlignmentPairDto
            {
                SourceAName = LabelOf(labelByKey, e.SourceA),
                SourceBName = LabelOf(labelByKey, e.SourceB),
                SharedGridCount = e.TotalMatches,
                ConsensusOffsetX = e.OffsetX,
                ConsensusOffsetY = e.OffsetY,
                Confidence = e.Consensus,
                Accepted = e.Accepted,
                RejectReason = e.RejectReason,
            });
        }

        foreach (var w in result.Warnings)
        {
            report.Conflicts.Add(new AlignmentConflictDto
            {
                Type = w.Type,
                Message = w.Message,
                SourceA = w.SourceA == null ? null : LabelOf(labelByKey, w.SourceA),
                SourceB = w.SourceB == null ? null : LabelOf(labelByKey, w.SourceB),
                Residual = w.Residual,
            });
        }

        await PersistAsync(report, cancellationToken);

        _logger.LogInformation(
            "Analyzed public map {PublicMapId}: {Sources} sources -> {Clusters} landmass(es), {Standalone} standalone, {Warnings} warning(s)",
            publicMapId, report.TotalSources, report.ClusterCount, report.StandaloneCount, report.WarningCount);

        return report;
    }

    public async Task<PublicMapAnalysisReportDto?> GetStoredAsync(string publicMapId, CancellationToken cancellationToken = default)
    {
        var row = await _db.PublicMapAnalyses
            .FirstOrDefaultAsync(a => a.PublicMapId == publicMapId, cancellationToken);
        if (row == null || string.IsNullOrEmpty(row.ReportJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<PublicMapAnalysisReportDto>(row.ReportJson, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize stored analysis for {PublicMapId}", publicMapId);
            return null;
        }
    }

    // ---- helpers ----

    private async Task PersistAsync(PublicMapAnalysisReportDto report, CancellationToken cancellationToken)
    {
        await _db.PublicMapAnalyses
            .Where(a => a.PublicMapId == report.PublicMapId)
            .ExecuteDeleteAsync(cancellationToken);

        _db.PublicMapAnalyses.Add(new PublicMapAnalysisEntity
        {
            PublicMapId = report.PublicMapId,
            AnalyzedAt = report.AnalyzedAt,
            AlignmentHash = report.AlignmentHash,
            ClusterCount = report.ClusterCount,
            StandaloneCount = report.StandaloneCount,
            EstMinX = report.EstMinX,
            EstMaxX = report.EstMaxX,
            EstMinY = report.EstMinY,
            EstMaxY = report.EstMaxY,
            EstZoom0TileCount = report.EstZoom0TileCount,
            EstTotalTileCount = report.EstTotalTileCount,
            WarningCount = report.WarningCount,
            ReportJson = JsonSerializer.Serialize(report, JsonOpts),
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Per-source metadata (type + friendly names) shared by clusters and pair/conflict labels.</summary>
    private sealed record SourceMeta(
        SourceGridSet GridSet, string SourceType, string DisplayLabel,
        string TenantId, string TenantName, int MapId, string MapName, int? HmapSourceId);

    /// <summary>Build display metadata for every tenant and hmap source.</summary>
    private async Task<List<SourceMeta>> BuildSourceMetasAsync(
        List<(PublicMapSourceEntity Source, SourceGridSet GridSet)> tenantLoaded,
        List<HmapLoadedSource> hmapLoaded,
        CancellationToken cancellationToken)
    {
        var metas = new List<SourceMeta>(tenantLoaded.Count + hmapLoaded.Count);

        var tenantIds = tenantLoaded.Select(t => t.Source.TenantId).Distinct().ToList();
        var tenantNames = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var mapIds = tenantLoaded.Select(t => t.Source.MapId).Distinct().ToList();
        var mapRows = await _db.Maps
            .IgnoreQueryFilters()
            .Where(m => tenantIds.Contains(m.TenantId) && mapIds.Contains(m.Id))
            .Select(m => new { m.Id, m.TenantId, m.Name })
            .ToListAsync(cancellationToken);
        var mapNames = mapRows.ToDictionary(m => (m.TenantId, m.Id), m => m.Name);

        foreach (var (source, gridSet) in tenantLoaded)
        {
            var tenantName = tenantNames.GetValueOrDefault(source.TenantId, source.TenantId);
            var mapName = mapNames.GetValueOrDefault((source.TenantId, source.MapId), $"Map {source.MapId}");
            metas.Add(new SourceMeta(gridSet, "Tenant", $"{tenantName} / {mapName}",
                source.TenantId, tenantName, source.MapId, mapName, null));
        }

        foreach (var h in hmapLoaded)
        {
            var name = h.File.Name;
            metas.Add(new SourceMeta(h.GridSet, "Hmap", $"HMap: {name}",
                string.Empty, "HMap", 0, name, h.Link.HmapSourceId));
        }

        return metas;
    }

    private static string LabelOf(Dictionary<string, string> labels, string key)
        => labels.GetValueOrDefault(key, key);

    private static (int? MinX, int? MaxX, int? MinY, int? MaxY, int Zoom0, int Total) EstimateBoundsAndTiles(
        IReadOnlyList<SourceGridSet> sets,
        IReadOnlyDictionary<string, (int X, int Y)> offsets)
    {
        int? minX = null, maxX = null, minY = null, maxY = null;
        var zoom0 = new HashSet<(int, int)>();
        foreach (var set in sets)
        {
            if (!offsets.TryGetValue(set.SourceKey, out var off)) continue;
            foreach (var (gx, gy) in set.Grids.Values)
            {
                int ux = gx + off.X, uy = gy + off.Y;
                minX = minX.HasValue ? Math.Min(minX.Value, ux) : ux;
                maxX = maxX.HasValue ? Math.Max(maxX.Value, ux) : ux;
                minY = minY.HasValue ? Math.Min(minY.Value, uy) : uy;
                maxY = maxY.HasValue ? Math.Max(maxY.Value, uy) : uy;
                zoom0.Add(((int)Math.Floor(ux / 4.0), (int)Math.Floor(uy / 4.0)));
            }
        }

        int total = zoom0.Count;
        var cur = zoom0;
        for (int z = 1; z <= 6; z++)
        {
            var parent = new HashSet<(int, int)>();
            foreach (var (x, y) in cur)
                parent.Add((x < 0 ? (x - 1) / 2 : x / 2, y < 0 ? (y - 1) / 2 : y / 2));
            if (parent.Count == 0) break;
            total += parent.Count;
            cur = parent;
        }

        return (minX, maxX, minY, maxY, zoom0.Count, total);
    }

    /// <summary>
    /// Stable SHA-256 over the source set + each source's grid content (ids + coords). Used to detect
    /// whether sources changed between a previewed analysis and a later generation.
    /// </summary>
    private static string ComputeAlignmentHash(IReadOnlyList<SourceGridSet> sets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var set in sets.OrderBy(s => s.SourceKey, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"S:{set.SourceKey}\n"));
            foreach (var g in set.Grids.OrderBy(g => g.Key, StringComparer.Ordinal))
                hash.AppendData(Encoding.UTF8.GetBytes($"{g.Key},{g.Value.X},{g.Value.Y}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
