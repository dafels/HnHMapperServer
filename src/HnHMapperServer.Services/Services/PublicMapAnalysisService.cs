using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Alignment;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<PublicMapAnalysisService> _logger;

    public PublicMapAnalysisService(
        ApplicationDbContext db,
        IAlignmentSolver solver,
        ILogger<PublicMapAnalysisService> logger)
    {
        _db = db;
        _solver = solver;
        _logger = logger;
    }

    public async Task<PublicMapAnalysisReportDto?> AnalyzeAsync(string publicMapId, CancellationToken cancellationToken = default)
    {
        var publicMap = await _db.PublicMaps.FirstOrDefaultAsync(p => p.Id == publicMapId, cancellationToken);
        if (publicMap == null) return null;

        var sources = await _db.PublicMapSources
            .Where(s => s.PublicMapId == publicMapId)
            .ToListAsync(cancellationToken);

        var loaded = await PublicMapSourceLoader.LoadAsync(_db, sources, cancellationToken);
        var labels = await BuildLabelsAsync(sources, cancellationToken);

        var result = _solver.Align(loaded.Select(l => l.GridSet).ToList());
        var keyToLoaded = loaded.ToDictionary(l => l.GridSet.SourceKey, l => l);

        var (minX, maxX, minY, maxY, zoom0, totalTiles) = EstimateBoundsAndTiles(loaded, result.Offsets);

        var report = new PublicMapAnalysisReportDto
        {
            PublicMapId = publicMapId,
            AnalyzedAt = DateTime.UtcNow,
            AlignmentHash = ComputeAlignmentHash(loaded),
            TotalSources = sources.Count,
            TotalGrids = loaded.Sum(l => l.GridSet.Grids.Count),
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
                if (!keyToLoaded.TryGetValue(key, out var l)) continue;
                var off = result.Offsets[key];
                var (tenantName, mapName) = labels.GetValueOrDefault(key, (l.Source.TenantId, $"Map {l.Source.MapId}"));
                dto.Sources.Add(new AlignmentSourceRefDto
                {
                    TenantId = l.Source.TenantId,
                    TenantName = tenantName,
                    MapId = l.Source.MapId,
                    MapName = mapName,
                    GridCount = l.GridSet.Grids.Count,
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
                SourceAName = LabelOf(labels, e.SourceA),
                SourceBName = LabelOf(labels, e.SourceB),
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
                SourceA = w.SourceA == null ? null : LabelOf(labels, w.SourceA),
                SourceB = w.SourceB == null ? null : LabelOf(labels, w.SourceB),
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

    /// <summary>Map each source key ("tenantId:mapId") to friendly (tenantName, mapName).</summary>
    private async Task<Dictionary<string, (string TenantName, string MapName)>> BuildLabelsAsync(
        List<PublicMapSourceEntity> sources, CancellationToken cancellationToken)
    {
        var tenantIds = sources.Select(s => s.TenantId).Distinct().ToList();
        var tenantNames = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var mapIds = sources.Select(s => s.MapId).Distinct().ToList();
        var mapRows = await _db.Maps
            .IgnoreQueryFilters()
            .Where(m => tenantIds.Contains(m.TenantId) && mapIds.Contains(m.Id))
            .Select(m => new { m.Id, m.TenantId, m.Name })
            .ToListAsync(cancellationToken);
        var mapNames = mapRows.ToDictionary(m => (m.TenantId, m.Id), m => m.Name);

        var labels = new Dictionary<string, (string, string)>();
        foreach (var s in sources)
        {
            var key = PublicMapSourceLoader.KeyFor(s.TenantId, s.MapId);
            var tenantName = tenantNames.GetValueOrDefault(s.TenantId, s.TenantId);
            var mapName = mapNames.GetValueOrDefault((s.TenantId, s.MapId), $"Map {s.MapId}");
            labels[key] = (tenantName, mapName);
        }
        return labels;
    }

    private static string LabelOf(Dictionary<string, (string TenantName, string MapName)> labels, string key)
        => labels.TryGetValue(key, out var l) ? $"{l.TenantName} / {l.MapName}" : key;

    private static (int? MinX, int? MaxX, int? MinY, int? MaxY, int Zoom0, int Total) EstimateBoundsAndTiles(
        List<(PublicMapSourceEntity Source, SourceGridSet GridSet)> loaded,
        IReadOnlyDictionary<string, (int X, int Y)> offsets)
    {
        int? minX = null, maxX = null, minY = null, maxY = null;
        var zoom0 = new HashSet<(int, int)>();
        foreach (var l in loaded)
        {
            if (!offsets.TryGetValue(l.GridSet.SourceKey, out var off)) continue;
            foreach (var (gx, gy) in l.GridSet.Grids.Values)
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
    private static string ComputeAlignmentHash(List<(PublicMapSourceEntity Source, SourceGridSet GridSet)> loaded)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var l in loaded.OrderBy(l => l.GridSet.SourceKey, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"S:{l.GridSet.SourceKey}\n"));
            foreach (var g in l.GridSet.Grids.OrderBy(g => g.Key, StringComparer.Ordinal))
                hash.AppendData(Encoding.UTF8.GetBytes($"{g.Key},{g.Value.X},{g.Value.Y}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
