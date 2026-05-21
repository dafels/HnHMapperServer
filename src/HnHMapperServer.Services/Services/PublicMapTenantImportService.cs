using System.Diagnostics;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Imports the shared PUBLIC map into a tenant's map by reading ONLY the snapshot that PUBLIC
/// regeneration committed:
///   - WebP tiles under {GridStorage}/public/{publicMapId}/0/ (400x400)
///   - markers.json (thingwall markers, absolute coords)
///   - PublicMapGridIndex rows (opaque grid ids per unified base-grid coord)
///
/// Never reads source tenants' Tiles/Grids/Markers. The PUBLIC map is treated as a black box
/// the same way the anonymous /public/{slug}/tiles endpoint serves it.
/// </summary>
public class PublicMapTenantImportService : IPublicMapTenantImportService
{
    private const string PreferredPublicMapId = IPublicMapTenantImportService.PreferredPublicMapId;
    private const int MinAlignmentMatches = 5;

    private readonly ApplicationDbContext _db;
    private readonly IMapNameService _mapNameService;
    private readonly IStorageQuotaService _quotaService;
    private readonly IHmapImportService _hmapImportService;
    private readonly IUpdateNotificationService _notifications;
    private readonly ITenantFilePathService _filePaths;
    private readonly ILogger<PublicMapTenantImportService> _logger;

    public PublicMapTenantImportService(
        ApplicationDbContext db,
        IMapNameService mapNameService,
        IStorageQuotaService quotaService,
        IHmapImportService hmapImportService,
        IUpdateNotificationService notifications,
        ITenantFilePathService filePaths,
        ILogger<PublicMapTenantImportService> logger)
    {
        _db = db;
        _mapNameService = mapNameService;
        _quotaService = quotaService;
        _hmapImportService = hmapImportService;
        _notifications = notifications;
        _filePaths = filePaths;
        _logger = logger;
    }

    public async Task<PublicMapImportResult> ImportAsync(
        string targetTenantId,
        int? targetMapId,
        string gridStorage,
        IProgress<HmapImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new PublicMapImportResult();
        var lockAcquired = false;

        // Rollback bookkeeping — only ever touches the target tenant's data.
        int? createdMapIdForRollback = null;
        var createdGridEntities = new List<GridDataEntity>();
        var createdTileEntities = new List<TileDataEntity>();
        var createdTileFiles = new List<string>();
        var createdMarkerEntities = new List<MarkerEntity>();
        double bytesAddedMB = 0;

        try
        {
            lockAcquired = await _hmapImportService.TryAcquireGlobalImportLockAsync(cancellationToken);
            if (!lockAcquired)
            {
                result.Success = false;
                result.ErrorCode = "lock_held";
                result.ErrorMessage = "Another import is already in progress. Please wait for it to complete and try again.";
                return result;
            }

            Report(progress, stopwatch, 1, "Loading PUBLIC map", 0, 1, "Resolving source", 0);

            // ---- Resolve PUBLIC map (prefer id == "public", else single active, else error) ----
            var publicMap = await ResolvePublicMapAsync(result, cancellationToken);
            if (publicMap == null) return result; // result already populated with the right error
            result.SourcePublicMapId = publicMap.Id;

            // ---- Load grid index (snapshot of per-coord gridIds) ----
            var index = await _db.PublicMapGridIndex
                .Where(g => g.PublicMapId == publicMap.Id)
                .Select(g => new { g.UnifiedX, g.UnifiedY, g.GridId, g.SnapshotCache })
                .ToListAsync(cancellationToken);

            if (index.Count == 0)
            {
                result.Success = false;
                result.ErrorCode = "index_missing";
                result.ErrorMessage = "This PUBLIC map predates the grid index. Ask a SuperAdmin to regenerate it before importing — otherwise character markers won't resolve to the imported map.";
                return result;
            }

            var indexByCoord = index.ToDictionary(e => (e.UnifiedX, e.UnifiedY), e => e.GridId);
            var indexByGridId = index.ToDictionary(e => e.GridId, e => (e.UnifiedX, e.UnifiedY));

            Report(progress, stopwatch, 1, "Loading PUBLIC map", 1, 1, $"{index.Count} indexed grids", 5);

            // ---- Resolve target map (create-new or use given existing) ----
            int finalMapId;
            int deltaX = 0;
            int deltaY = 0;

            if (targetMapId.HasValue)
            {
                // Validate the chosen target belongs to the calling tenant.
                var targetExists = await _db.Maps
                    .IgnoreQueryFilters()
                    .AnyAsync(m => m.Id == targetMapId.Value && m.TenantId == targetTenantId, cancellationToken);
                if (!targetExists)
                {
                    result.Success = false;
                    result.ErrorCode = "no_target_map";
                    result.ErrorMessage = $"Map {targetMapId.Value} doesn't exist in this tenant.";
                    return result;
                }

                finalMapId = targetMapId.Value;
                result.CreatedNewMap = false;

                Report(progress, stopwatch, 2, "Aligning to target map", 0, 1, "Computing offset", 6);

                var (delta, matches) = await ComputeAlignmentDeltaAsync(
                    targetTenantId, finalMapId, indexByGridId, cancellationToken);

                if (matches < MinAlignmentMatches)
                {
                    result.Success = false;
                    result.ErrorCode = "no_overlap";
                    result.ErrorMessage = $"Couldn't align the PUBLIC map with the chosen map — only {matches} shared grid id(s) found, need at least {MinAlignmentMatches}. Try creating a new map instead.";
                    return result;
                }

                deltaX = delta.X;
                deltaY = delta.Y;
                _logger.LogInformation(
                    "Aligned PUBLIC import to tenant {Tenant} map {Map}: delta=({Dx},{Dy}) from {Matches} shared grids",
                    targetTenantId, finalMapId, deltaX, deltaY, matches);

                Report(progress, stopwatch, 2, "Aligning to target map", 1, 1, $"{matches} shared grids, delta=({deltaX},{deltaY})", 7);
            }
            else
            {
                finalMapId = await CreateNewTargetMapAsync(targetTenantId, cancellationToken);
                createdMapIdForRollback = finalMapId;
                result.CreatedNewMap = true;
                _logger.LogInformation("Created new map {Map} in tenant {Tenant} for PUBLIC import", finalMapId, targetTenantId);
            }
            result.TargetMapId = finalMapId;

            // ---- Quota preflight (sum of zoom-0 WebP file sizes on disk) ----
            var zoom0Dir = Path.Combine(gridStorage, "public", publicMap.Id, "0");
            if (!Directory.Exists(zoom0Dir))
            {
                result.Success = false;
                result.ErrorCode = "no_public_map";
                result.ErrorMessage = "The PUBLIC map has no zoom-0 tiles on disk — it may need to be regenerated.";
                return result;
            }

            var webpFiles = Directory.GetFiles(zoom0Dir, "*.webp");
            var estimatedBytes = webpFiles.Sum(f => new FileInfo(f).Length);
            var estimatedMB = estimatedBytes / (1024.0 * 1024.0);
            if (estimatedMB > 0)
            {
                var quotaOk = await _quotaService.CheckQuotaAsync(targetTenantId, estimatedMB);
                if (!quotaOk)
                {
                    result.Success = false;
                    result.ErrorCode = "quota_exceeded";
                    result.ErrorMessage = $"This import would add about {estimatedMB:F1} MB, which exceeds the tenant storage quota.";
                    return result;
                }
            }

            // ---- Skip-if-exists set: tile coords already present in the target map at zoom 0 ----
            var existingCoordRows = await _db.Tiles
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == targetTenantId && t.MapId == finalMapId && t.Zoom == 0)
                .Select(t => new { t.CoordX, t.CoordY })
                .ToListAsync(cancellationToken);
            var existingTargetCoords = new HashSet<(int X, int Y)>(
                existingCoordRows.Select(r => (r.CoordX, r.CoordY)));

            // Grid ids the tenant ALREADY HAS anywhere (any map). The Grids PK is
            // (Id, TenantId) — tenant-wide unique — so inserting an id that already exists
            // anywhere in this tenant violates the constraint. If a tenant has already
            // explored a world location, the game client has been uploading that same
            // content-hash id into whichever map it landed on; we must not duplicate it.
            var existingTenantGridIds = await _db.Grids
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == targetTenantId)
                .Select(g => g.Id)
                .ToHashSetAsync(cancellationToken);

            // ---- Decompose 400x400 WebPs → 16 100x100 PNGs each, using index gridIds ----
            Directory.CreateDirectory(Path.Combine(gridStorage, "tenants", targetTenantId, finalMapId.ToString(), "0"));

            // Track every zoom-0 coord we wrote a tile to, so the zoom regen can build zoom 1-6
            // even for coords whose grids live on another tenant map (no grid row in this map).
            var writtenZoom0Coords = new HashSet<(int X, int Y)>();

            var nowCache = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long bytesAdded = 0;
            var totalFiles = webpFiles.Length;
            var processedFiles = 0;

            foreach (var webpPath in webpFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedFiles++;

                // Filename: {px}_{py}.webp
                var name = Path.GetFileNameWithoutExtension(webpPath);
                var parts = name.Split('_');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var px) || !int.TryParse(parts[1], out var py))
                {
                    _logger.LogWarning("Skipping malformed PUBLIC tile filename: {File}", webpPath);
                    continue;
                }

                using var img = await Image.LoadAsync<Rgba32>(webpPath, cancellationToken);
                if (img.Width != 400 || img.Height != 400)
                {
                    _logger.LogWarning("Unexpected PUBLIC tile dimensions {W}x{H} for {File}", img.Width, img.Height, webpPath);
                    continue;
                }

                for (int dx = 0; dx < 4; dx++)
                {
                    for (int dy = 0; dy < 4; dy++)
                    {
                        var gx = px * 4 + dx;
                        var gy = py * 4 + dy;
                        var finalX = gx + deltaX;
                        var finalY = gy + deltaY;

                        // No index entry → that sub-region had no source grid; skip.
                        if (!indexByCoord.TryGetValue((gx, gy), out var snapshotGridId))
                        {
                            result.TilesSkipped++;
                            continue;
                        }

                        // Skip-if-exists at target coord (merge-mode preservation).
                        if (existingTargetCoords.Contains((finalX, finalY)))
                        {
                            result.TilesSkipped++;
                            continue;
                        }

                        // Defense in depth: skip fully transparent sub-tiles even if indexed.
                        if (IsSubTileFullyTransparent(img, dx * 100, dy * 100))
                        {
                            result.TilesSkipped++;
                            continue;
                        }

                        // If the tenant already has this gridId somewhere (any map), we MUST NOT
                        // insert another row — Grids.PK is (Id, TenantId). But we can still write
                        // the tile in the target map: tiles are keyed by (MapId, Coord, Zoom) and
                        // don't reference grids by id. Character positions stay resolved to whichever
                        // map already owns this grid (game client → existing grid row → its Map).
                        var gridAlreadyInTenant = existingTenantGridIds.Contains(snapshotGridId);

                        // Crop the 100x100 region and save as PNG in target tenant storage.
                        using var subTile = img.Clone(ctx => ctx.Crop(new Rectangle(dx * 100, dy * 100, 100, 100)));

                        var targetCoord = $"{finalX}_{finalY}";
                        var targetRelative = _filePaths.GetTileRelativePath(targetTenantId, finalMapId, 0, targetCoord);
                        var targetFull = Path.Combine(gridStorage, targetRelative);
                        await subTile.SaveAsPngAsync(targetFull, cancellationToken);
                        createdTileFiles.Add(targetFull);

                        var fileSize = (int)new FileInfo(targetFull).Length;
                        bytesAdded += fileSize;

                        // Insert Grid row only if the tenant doesn't already have this id
                        // anywhere — keeps the (Id, TenantId) unique constraint intact. The
                        // tile still gets inserted below regardless, so the new map is
                        // visually complete even if the character mapping stays with another map.
                        if (!gridAlreadyInTenant)
                        {
                            var gridEntity = new GridDataEntity
                            {
                                Id = snapshotGridId,
                                CoordX = finalX,
                                CoordY = finalY,
                                Map = finalMapId,
                                NextUpdate = DateTime.UtcNow,
                                TenantId = targetTenantId
                            };
                            _db.Grids.Add(gridEntity);
                            createdGridEntities.Add(gridEntity);
                            existingTenantGridIds.Add(snapshotGridId);
                            result.CreatedGridIds.Add(snapshotGridId);
                        }

                        var tileEntity = new TileDataEntity
                        {
                            MapId = finalMapId,
                            CoordX = finalX,
                            CoordY = finalY,
                            Zoom = 0,
                            File = targetRelative,
                            Cache = nowCache,
                            TenantId = targetTenantId,
                            FileSizeBytes = fileSize
                        };
                        _db.Tiles.Add(tileEntity);
                        createdTileEntities.Add(tileEntity);
                        existingTargetCoords.Add((finalX, finalY));
                        writtenZoom0Coords.Add((finalX, finalY));
                        result.TilesAdded++;

                        if (createdTileEntities.Count % 200 == 0)
                        {
                            await _db.SaveChangesAsync(cancellationToken);
                        }
                    }
                }

                if (processedFiles % 5 == 0 || processedFiles == totalFiles)
                {
                    ReportTilePhase(progress, stopwatch, processedFiles, totalFiles, $"PUBLIC tile ({px},{py})");
                }
            }

            await _db.SaveChangesAsync(cancellationToken);

            bytesAddedMB = bytesAdded / (1024.0 * 1024.0);
            if (bytesAddedMB > 0)
            {
                await _quotaService.IncrementStorageUsageAsync(targetTenantId, bytesAddedMB);
            }
            result.BytesAdded = bytesAdded;

            // ---- Generate zoom 1-6 for the new/affected target map ----
            // Pass the explicit list of coords we wrote tiles for, so zoom regen also runs for
            // coords whose grid rows live on another tenant map (Option F path). Without this,
            // zoom 1-6 would only cover coords whose grids actually got inserted into this map.
            Report(progress, stopwatch, 4, "Generating zoom levels", 0, 1, "Zoom 1-6", 80);
            // Include any pre-existing target coords too, so a merge that overlaps with
            // already-present tiles still regenerates zoom 1-6 across the affected area.
            var allZoom0CoordsForZoomGen = new HashSet<(int X, int Y)>(writtenZoom0Coords);
            foreach (var c in existingTargetCoords) allZoom0CoordsForZoomGen.Add(c);
            await _hmapImportService.GenerateZoomLevelsForMapAsync(
                finalMapId, targetTenantId, gridStorage, cancellationToken,
                allZoom0CoordsForZoomGen.Count > 0 ? allZoom0CoordsForZoomGen : null);
            Report(progress, stopwatch, 4, "Generating zoom levels", 1, 1, "Done", 95);

            // ---- Markers from markers.json (thingwalls in absolute coords) ----
            await ImportThingwallMarkersFromJsonAsync(
                publicMap.Id, gridStorage, deltaX, deltaY, targetTenantId, finalMapId,
                createdMarkerEntities, result, progress, stopwatch, cancellationToken);

            foreach (var m in createdMarkerEntities)
                result.CreatedMarkerIds.Add(m.Id);

            // ---- Broadcast SSE so map viewers refresh ----
            var savedMap = await _db.Maps
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == finalMapId && m.TenantId == targetTenantId, cancellationToken);
            if (savedMap != null)
            {
                _notifications.NotifyMapUpdated(new MapInfo
                {
                    Id = savedMap.Id,
                    Name = savedMap.Name,
                    Hidden = savedMap.Hidden,
                    Priority = savedMap.Priority,
                    CreatedAt = savedMap.CreatedAt,
                    DefaultStartX = savedMap.DefaultStartX,
                    DefaultStartY = savedMap.DefaultStartY,
                    TenantId = savedMap.TenantId
                });
            }
            _notifications.NotifyMapRevision(finalMapId, (int)(nowCache & 0x7FFFFFFF));

            result.Success = true;
            result.Duration = stopwatch.Elapsed;

            _logger.LogInformation(
                "PUBLIC map import into tenant {Tenant} map {Map}: +{TilesAdded}/-{TilesSkipped} tiles, +{MarkersAdded}/-{MarkersSkipped} markers, {Bytes}B, {Duration:F1}s",
                targetTenantId, finalMapId, result.TilesAdded, result.TilesSkipped,
                result.MarkersAdded, result.MarkersSkipped, bytesAdded, result.Duration.TotalSeconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PUBLIC map import canceled for tenant {Tenant}", targetTenantId);
            await RollbackAsync(targetTenantId, createdMapIdForRollback, createdGridEntities, createdTileEntities,
                createdTileFiles, createdMarkerEntities, bytesAddedMB, gridStorage);
            result.Success = false;
            result.ErrorCode = "canceled";
            result.ErrorMessage = "Import was canceled.";
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PUBLIC map import failed for tenant {Tenant}", targetTenantId);
            await RollbackAsync(targetTenantId, createdMapIdForRollback, createdGridEntities, createdTileEntities,
                createdTileFiles, createdMarkerEntities, bytesAddedMB, gridStorage);
            result.Success = false;
            result.ErrorCode ??= "import_failed";
            result.ErrorMessage = ex.Message;
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        finally
        {
            if (lockAcquired)
            {
                _hmapImportService.ReleaseGlobalImportLock();
            }
        }
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private async Task<Infrastructure.Data.PublicMapEntity?> ResolvePublicMapAsync(
        PublicMapImportResult result, CancellationToken cancellationToken)
    {
        var activePublicMaps = await _db.PublicMaps
            .Where(p => p.IsActive)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var publicMap = activePublicMaps.FirstOrDefault(p => p.Id == PreferredPublicMapId);
        if (publicMap != null) return publicMap;

        if (activePublicMaps.Count == 1) return activePublicMaps[0];

        if (activePublicMaps.Count == 0)
        {
            result.Success = false;
            result.ErrorCode = "no_public_map";
            result.ErrorMessage = "No active PUBLIC map exists to import from.";
            return null;
        }

        result.Success = false;
        result.ErrorCode = "ambiguous_public_map";
        result.ErrorMessage = $"Multiple active PUBLIC maps exist and none is named '{PreferredPublicMapId}'. Ask a SuperAdmin to designate the canonical one.";
        return null;
    }

    private async Task<int> CreateNewTargetMapAsync(string targetTenantId, CancellationToken cancellationToken)
    {
        var name = await _mapNameService.GenerateUniqueIdentifierAsync(targetTenantId);

        // Insert MapInfoEntity directly so we control the TenantId explicitly (avoids any
        // dependency on the HttpContext tenant accessor matching the import target).
        var entity = new Infrastructure.Data.MapInfoEntity
        {
            Id = 0,
            Name = name,
            Hidden = false,
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            TenantId = targetTenantId
        };
        _db.Maps.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    /// <summary>
    /// Compute median (deltaX, deltaY) between the PUBLIC's unified coord space and the
    /// target tenant map by joining <c>PublicMapGridIndex.GridId</c> against the target's
    /// own <c>Grids.Id</c>. Returns the delta and the number of matches.
    /// </summary>
    private async Task<((int X, int Y) Delta, int Matches)> ComputeAlignmentDeltaAsync(
        string targetTenantId,
        int targetMapId,
        Dictionary<string, (int X, int Y)> indexByGridId,
        CancellationToken cancellationToken)
    {
        if (indexByGridId.Count == 0) return ((0, 0), 0);

        var indexGridIds = indexByGridId.Keys.ToList();

        // Read only the target tenant's own grids. No cross-tenant DB reads.
        var targetGrids = await _db.Grids
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == targetTenantId
                        && g.Map == targetMapId
                        && indexGridIds.Contains(g.Id))
            .Select(g => new { g.Id, g.CoordX, g.CoordY })
            .ToListAsync(cancellationToken);

        if (targetGrids.Count == 0) return ((0, 0), 0);

        var dxs = new List<int>(targetGrids.Count);
        var dys = new List<int>(targetGrids.Count);
        foreach (var tg in targetGrids)
        {
            var src = indexByGridId[tg.Id];
            dxs.Add(tg.CoordX - src.X);
            dys.Add(tg.CoordY - src.Y);
        }

        return ((Median(dxs), Median(dys)), targetGrids.Count);
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    private static bool IsSubTileFullyTransparent(Image<Rgba32> img, int originX, int originY)
    {
        // Sample alpha across the 100x100 region. If any pixel is non-transparent, keep it.
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                if (img[originX + x, originY + y].A != 0)
                    return false;
            }
        }
        return true;
    }

    private async Task ImportThingwallMarkersFromJsonAsync(
        string publicMapId,
        string gridStorage,
        int deltaX,
        int deltaY,
        string targetTenantId,
        int targetMapId,
        List<MarkerEntity> createdMarkers,
        PublicMapImportResult result,
        IProgress<HmapImportProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Report(progress, stopwatch, 5, "Importing thingwall markers", 0, 1, "Reading markers.json", 96);

        var markersPath = Path.Combine(gridStorage, "public", publicMapId, "markers.json");
        if (!File.Exists(markersPath))
        {
            _logger.LogInformation("PUBLIC {Id} has no markers.json — skipping marker import", publicMapId);
            return;
        }

        List<PublicMapMarkerDto>? markers;
        try
        {
            var json = await File.ReadAllTextAsync(markersPath, cancellationToken);
            markers = JsonSerializer.Deserialize<List<PublicMapMarkerDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse markers.json for PUBLIC {Id} — skipping marker import", publicMapId);
            return;
        }

        if (markers == null || markers.Count == 0) return;

        // After import, map (coord) → gridId in the target so we can attach markers correctly.
        var targetGridByCoord = await _db.Grids
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == targetTenantId && g.Map == targetMapId)
            .Select(g => new { g.Id, g.CoordX, g.CoordY })
            .ToListAsync(cancellationToken);
        var gridByCoord = targetGridByCoord
            .GroupBy(g => (g.CoordX, g.CoordY))
            .ToDictionary(grp => grp.Key, grp => grp.First().Id);

        // Dedup against pre-existing markers in the target (any map; key is per-tenant unique).
        var existingKeys = await _db.Markers
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == targetTenantId)
            .Select(m => m.Key)
            .ToHashSetAsync(cancellationToken);

        foreach (var m in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Floor-divide so negative coords map correctly.
            var gridX = (int)Math.Floor(m.X / 100.0);
            var gridY = (int)Math.Floor(m.Y / 100.0);
            var posX = ((m.X % 100) + 100) % 100;
            var posY = ((m.Y % 100) + 100) % 100;

            var finalGridCoord = (X: gridX + deltaX, Y: gridY + deltaY);
            if (!gridByCoord.TryGetValue(finalGridCoord, out var targetGridId))
            {
                result.MarkersSkipped++;
                continue;
            }

            var key = $"{targetGridId}_{posX}_{posY}";
            if (existingKeys.Contains(key))
            {
                result.MarkersSkipped++;
                continue;
            }

            var marker = new MarkerEntity
            {
                Key = key,
                Name = string.IsNullOrEmpty(m.Name) ? "Thingwall" : m.Name,
                GridId = targetGridId,
                PositionX = posX,
                PositionY = posY,
                Image = string.IsNullOrEmpty(m.Image) ? "gfx/terobjs/mm/thingwall" : m.Image,
                Hidden = false,
                MaxReady = -1,
                MinReady = -1,
                Ready = false,
                TenantId = targetTenantId
            };
            _db.Markers.Add(marker);
            createdMarkers.Add(marker);
            existingKeys.Add(key);
            result.MarkersAdded++;
        }

        if (createdMarkers.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        Report(progress, stopwatch, 5, "Importing thingwall markers",
            result.MarkersAdded + result.MarkersSkipped, markers.Count,
            $"{result.MarkersAdded} added, {result.MarkersSkipped} skipped", 100);
    }

    private async Task RollbackAsync(
        string targetTenantId,
        int? createdMapId,
        List<GridDataEntity> createdGrids,
        List<TileDataEntity> createdTiles,
        List<string> createdTileFiles,
        List<MarkerEntity> createdMarkers,
        double bytesAddedMB,
        string gridStorage)
    {
        try
        {
            foreach (var m in createdMarkers) _db.Markers.Remove(m);
            foreach (var t in createdTiles) _db.Tiles.Remove(t);
            foreach (var g in createdGrids) _db.Grids.Remove(g);

            try { await _db.SaveChangesAsync(); }
            catch (Exception saveEx) { _logger.LogWarning(saveEx, "Rollback save-changes failed for tenant {Tenant}", targetTenantId); }

            foreach (var file in createdTileFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception fx) { _logger.LogWarning(fx, "Failed to delete rollback tile file {File}", file); }
            }

            if (createdMapId.HasValue)
            {
                var mapDir = Path.Combine(gridStorage, "tenants", targetTenantId, createdMapId.Value.ToString());
                if (Directory.Exists(mapDir))
                {
                    try { Directory.Delete(mapDir, recursive: true); }
                    catch (Exception dx) { _logger.LogWarning(dx, "Failed to delete rollback map directory {Dir}", mapDir); }
                }

                var mapEntity = await _db.Maps
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(m => m.Id == createdMapId.Value && m.TenantId == targetTenantId);
                if (mapEntity != null)
                {
                    _db.Maps.Remove(mapEntity);
                    await _db.SaveChangesAsync();
                }
            }

            if (bytesAddedMB > 0)
            {
                try { await _quotaService.DecrementStorageUsageAsync(targetTenantId, bytesAddedMB); }
                catch (Exception qx) { _logger.LogWarning(qx, "Failed to decrement quota during rollback for tenant {Tenant}", targetTenantId); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback handler failed for tenant {Tenant}", targetTenantId);
        }
    }

    // ----------------------------------------------------------------------
    // Progress reporting (compatible with HmapImportProgress so the SSE UI is shared)
    // ----------------------------------------------------------------------

    private const int TotalPhases = 5;

    private static void Report(
        IProgress<HmapImportProgress>? progress,
        Stopwatch stopwatch,
        int phaseNumber,
        string phaseName,
        int current,
        int total,
        string itemName,
        double overallPercent)
    {
        progress?.Report(new HmapImportProgress
        {
            Phase = phaseName,
            CurrentItem = current,
            TotalItems = total,
            CurrentItemName = itemName,
            PhaseNumber = phaseNumber,
            TotalPhases = TotalPhases,
            OverallPercent = overallPercent,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            ItemsPerSecond = stopwatch.Elapsed.TotalSeconds > 0.5 ? current / stopwatch.Elapsed.TotalSeconds : 0
        });
    }

    private static void ReportTilePhase(IProgress<HmapImportProgress>? progress, Stopwatch stopwatch, int current, int total, string itemName)
    {
        // Phase 3 spans 7% → 80% of overall progress.
        double overall = total > 0 ? 7 + (73.0 * current / total) : 7;
        Report(progress, stopwatch, 3, "Copying tiles", current, total, itemName, overall);
    }
}
