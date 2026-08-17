using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class GridService : IGridService
{
    private readonly IGridRepository _gridRepository;
    private readonly IMapRepository _mapRepository;
    private readonly ITileService _tileService;
    private readonly IConfigRepository _configRepository;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IMapNameService _mapNameService;
    private readonly IPendingMarkerService _pendingMarkerService;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ILogger<GridService> _logger;

    public GridService(
        IGridRepository gridRepository,
        IMapRepository mapRepository,
        ITileService tileService,
        IConfigRepository configRepository,
        IUpdateNotificationService updateNotificationService,
        IMapNameService mapNameService,
        IPendingMarkerService pendingMarkerService,
        ITenantContextAccessor tenantContext,
        ILogger<GridService> logger)
    {
        _gridRepository = gridRepository;
        _mapRepository = mapRepository;
        _tileService = tileService;
        _configRepository = configRepository;
        _updateNotificationService = updateNotificationService;
        _mapNameService = mapNameService;
        _pendingMarkerService = pendingMarkerService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// Merges (which delete the source map — irreversible) only run when each involved map is
    /// witnessed by at least this many agreeing grids in the current matrix. A single witness may
    /// itself be a misplaced grid.
    /// </summary>
    private const int MIN_MERGE_WITNESSES = 2;

    /// <summary>
    /// Client matrices mark not-yet-loaded cells with id "0" (or leave them empty). Treating those
    /// as real grids is what let a stored "0" row anchor unrelated world regions together.
    /// </summary>
    private static bool IsPlaceholderGridId(string? gridId) =>
        string.IsNullOrWhiteSpace(gridId) || gridId.Trim() == "0";

    /// <summary>Known grids of one map observed in the current matrix.</summary>
    private sealed class MapWitness
    {
        public HashSet<Coord> Offsets { get; } = new();
        public List<(string GridId, int X, int Y, Coord Stored)> Grids { get; } = new();
    }

    private static GridRequestDto EmptyResponse() => new()
    {
        GridRequests = new List<string>(),
        Map = 0,
        Coords = new Coord(0, 0)
    };

    public async Task<GridRequestDto> ProcessGridUpdateAsync(GridUpdateDto gridUpdate, string gridStorage)
    {
        var gridRequests = new List<string>();
        var knownByPos = new Dictionary<(int X, int Y), GridData>();
        var anchors = new Dictionary<int, MapWitness>();
        var hasRealCell = false;

        // First pass: find which maps these grids belong to. Placeholder cells are holes — they
        // never anchor and are never inserted.
        for (int x = 0; x < gridUpdate.Grids.Count; x++)
        {
            for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
            {
                var gridId = gridUpdate.Grids[x][y];
                if (IsPlaceholderGridId(gridId))
                    continue;

                hasRealCell = true;
                var gridData = await _gridRepository.GetGridAsync(gridId);
                if (gridData == null)
                    continue;

                knownByPos[(x, y)] = gridData;
                if (!anchors.TryGetValue(gridData.Map, out var witness))
                {
                    witness = new MapWitness();
                    anchors[gridData.Map] = witness;
                }
                witness.Offsets.Add(new Coord(gridData.Coord.X - x, gridData.Coord.Y - y));
                witness.Grids.Add((gridId, x, y, gridData.Coord));
            }
        }

        // All known grids of one map must imply the same matrix offset. A contradiction means the
        // matrix mixes coordinate frames (client glitch or already-corrupted anchors) — writing
        // anything would spread the corruption, so the whole update is rejected.
        if (anchors.Any(kv => kv.Value.Offsets.Count > 1))
        {
            var matrix = "[" + string.Join(",", gridUpdate.Grids.Select(row => "[" + string.Join(",", row) + "]")) + "]";
            var anchorDump = string.Join("; ", anchors.SelectMany(kv => kv.Value.Grids.Select(g =>
                $"{g.GridId}@matrix({g.X},{g.Y}) -> map {kv.Key} stored({g.Stored.X},{g.Stored.Y}) offset({g.Stored.X - g.X},{g.Stored.Y - g.Y})")));
            _logger.LogWarning(
                "GridUpdate REJECTED for tenant {TenantId}: conflicting offsets within one matrix. Matrix={Matrix}. Anchors={Anchors}",
                _tenantContext.GetRequiredTenantId(), matrix, anchorDump);
            return EmptyResponse();
        }

        // No existing maps - create a new one
        if (anchors.Count == 0)
        {
            if (!hasRealCell)
            {
                _logger.LogInformation("GridUpdate no-op for tenant {TenantId}: matrix contains only placeholder ids",
                    _tenantContext.GetRequiredTenantId());
                return EmptyResponse();
            }

            var config = await _configRepository.GetConfigAsync();
            var tenantId = _tenantContext.GetRequiredTenantId();

            // Check if new map creation is allowed for this tenant
            if (!config.AllowNewMaps)
            {
                _logger.LogWarning("New map creation is disabled for tenant {TenantId}", tenantId);
                throw new InvalidOperationException("New map creation is disabled for this tenant");
            }

            // Generate unique icon-based name (e.g., "arrow-wagon-4273")
            var mapName = await _mapNameService.GenerateUniqueIdentifierAsync(tenantId);

            var mapInfo = new MapInfo
            {
                Id = 0,  // Let SQLite AUTOINCREMENT assign ID atomically (prevents race conditions)
                Name = mapName,  // Icon-based name like "arrow-wagon-4273"
                Hidden = config.DefaultHide,
                Priority = -1,  // New maps start with priority -1 so they appear below priority 0 maps
                CreatedAt = DateTime.UtcNow  // Track creation time for auto-cleanup of empty maps
            };

            await _mapRepository.SaveMapAsync(mapInfo);

            var newMapId = mapInfo.Id;  // Use the auto-generated ID

            // SaveMapAsync persists the tenant from the tenant context but does not set it
            // back on the domain object — the SSE loop filters events by TenantId.
            mapInfo.TenantId = tenantId;
            _updateNotificationService.NotifyMapUpdated(mapInfo);

            _logger.LogInformation("Client created new map {MapId} with name '{MapName}'", newMapId, mapName);

            for (int x = 0; x < gridUpdate.Grids.Count; x++)
            {
                for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
                {
                    var gridId = gridUpdate.Grids[x][y];
                    if (IsPlaceholderGridId(gridId))
                        continue;

                    var gridData = new GridData
                    {
                        Id = gridId,
                        Map = newMapId,
                        Coord = new Coord(x - 1, y - 1),
                        NextUpdate = DateTime.UtcNow.AddMinutes(-1) // Set to past so grid is immediately requestable
                    };

                    await _gridRepository.SaveGridAsync(gridData);
                    gridRequests.Add(gridId);

                    // Process any pending markers waiting for this grid
                    var activated = await _pendingMarkerService.ProcessPendingMarkersForGridAsync(tenantId, gridId);
                    if (activated > 0)
                    {
                        _logger.LogInformation("Activated {Count} pending markers for grid {GridId}", activated, gridId);
                    }
                }
            }

            var newMapResponse = new GridRequestDto
            {
                GridRequests = gridRequests,
                Map = newMapId,
                Coords = new Coord(0, 0)
            };

            _logger.LogInformation("GridUpdate response (new map): {GridCount} grids requested for map {MapId}",
                newMapResponse.GridRequests.Count, newMapResponse.Map);

            return newMapResponse;
        }

        // Find the target map (priority map or lowest ID)
        int targetMapId = -1;
        (int X, int Y) offset = (0, 0);

        int maxPriority = -1;
        foreach (var (mapId, witness) in anchors)
        {
            var mapInfo = await _mapRepository.GetMapAsync(mapId);
            var single = witness.Offsets.Single(); // guaranteed by the consistency guard above
            var off = (single.X, single.Y);
            // Choose map with highest priority value (if any have priority > 0)
            if (mapInfo != null && mapInfo.Priority > 0 && mapInfo.Priority > maxPriority)
            {
                maxPriority = mapInfo.Priority;
                targetMapId = mapId;
                offset = off;
            }
            else if (maxPriority == -1 && (targetMapId == -1 || mapId < targetMapId))
            {
                // Fallback to lowest ID if no priority maps
                targetMapId = mapId;
                offset = off;
            }
        }

        _logger.LogInformation("Client in map {MapId}", targetMapId);

        // One batched lookup of every cell this matrix could claim on the target map, so inserts
        // can refuse cells already owned by a different grid id. TryAdd, not ToDictionary: a
        // previously corrupted database may hold several grids on one cell.
        var rowCount = gridUpdate.Grids.Count;
        var colCount = gridUpdate.Grids.Max(row => row.Count);
        var occupants = await _gridRepository.GetGridsByMapInAreaAsync(
            targetMapId, offset.X, offset.Y, offset.X + rowCount - 1, offset.Y + colCount - 1);
        var ownerByCell = new Dictionary<Coord, string>();
        foreach (var occupant in occupants)
            ownerByCell.TryAdd(occupant.Coord, occupant.Id);

        // Process grids
        for (int x = 0; x < gridUpdate.Grids.Count; x++)
        {
            for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
            {
                var gridId = gridUpdate.Grids[x][y];
                if (IsPlaceholderGridId(gridId))
                    continue;

                if (knownByPos.TryGetValue((x, y), out var existing))
                {
                    if (DateTime.UtcNow >= existing.NextUpdate)
                    {
                        gridRequests.Add(gridId);
                    }
                    continue;
                }

                var dest = new Coord(x + offset.X, y + offset.Y);
                if (ownerByCell.TryGetValue(dest, out var ownerId) && ownerId != gridId)
                {
                    _logger.LogWarning(
                        "Skipped grid insert for tenant {TenantId}: cell ({X},{Y}) on map {MapId} already owned by grid {OwnerGridId}; incoming grid {GridId} not inserted",
                        _tenantContext.GetRequiredTenantId(), dest.X, dest.Y, targetMapId, ownerId, gridId);
                    continue;
                }

                var gridData = new GridData
                {
                    Id = gridId,
                    Map = targetMapId,
                    Coord = dest,
                    NextUpdate = DateTime.UtcNow.AddMinutes(-1) // Set to past so grid is immediately requestable
                };

                await _gridRepository.SaveGridAsync(gridData);
                ownerByCell[dest] = gridId;
                gridRequests.Add(gridId);

                // Process any pending markers waiting for this grid
                var currentTenantId = _tenantContext.GetRequiredTenantId();
                var activated = await _pendingMarkerService.ProcessPendingMarkersForGridAsync(currentTenantId, gridId);
                if (activated > 0)
                {
                    _logger.LogInformation("Activated {Count} pending markers for grid {GridId}", activated, gridId);
                }
            }
        }

        // Center grid for response — pass-1 result when known, else the cell this update would
        // have placed it at (strictly better than the old (0,0) fallback).
        knownByPos.TryGetValue((1, 1), out var centerGrid);

        var response = new GridRequestDto
        {
            GridRequests = gridRequests,
            Map = centerGrid?.Map ?? targetMapId,
            Coords = centerGrid?.Coord ?? new Coord(1 + offset.X, 1 + offset.Y)
        };

        _logger.LogInformation("GridUpdate response (existing map): {GridCount} grids requested for map {MapId}, coords {Coords}",
            response.GridRequests.Count, response.Map, response.Coords);

        // Handle map merging if multiple maps detected — but only between maps that are each
        // witnessed by at least MIN_MERGE_WITNESSES agreeing grids in THIS matrix. Merges delete
        // the source map, so a single (possibly misplaced) witness is not enough evidence; a
        // deferred merge simply lands on a later matrix with more witnesses.
        if (anchors.Count > 1)
        {
            var eligible = new Dictionary<int, (int X, int Y)>();
            foreach (var (mapId, witness) in anchors)
            {
                if (witness.Grids.Count >= MIN_MERGE_WITNESSES)
                {
                    var single = witness.Offsets.Single();
                    eligible[mapId] = (single.X, single.Y);
                }
                else
                {
                    _logger.LogWarning(
                        "Map merge deferred for tenant {TenantId}: map {MapId} witnessed by only {Count} grid(s) in this matrix (need >= {Min} agreeing); maps in matrix: {AllMapIds}",
                        _tenantContext.GetRequiredTenantId(), mapId, witness.Grids.Count, MIN_MERGE_WITNESSES,
                        string.Join(",", anchors.Keys));
                }
            }

            if (eligible.ContainsKey(targetMapId) && eligible.Count > 1)
            {
                _logger.LogInformation("Merging {MapCount} maps into target map {TargetMapId}", eligible.Count, targetMapId);
                await MergeMapsAsync(eligible, targetMapId, offset, gridStorage);
            }
        }

        return response;
    }

    private async Task MergeMapsAsync(
        Dictionary<int, (int X, int Y)> maps,
        int targetMapId,
        (int X, int Y) targetOffset,
        string gridStorage)
    {
        var allGrids = await _gridRepository.GetAllGridsAsync();
        var tenantId = _tenantContext.GetRequiredTenantId();

        // Track tiles that need zoom regeneration (targetMapId, targetCoord)
        var tilesToRegenerate = new List<(int mapId, Coord coord)>();

        foreach (var (sourceMapId, sourceOffset) in maps)
        {
            if (sourceMapId == targetMapId)
                continue;

            var shift = new Coord(
                targetOffset.X - sourceOffset.X,
                targetOffset.Y - sourceOffset.Y);

            // Update all grids from source map to target map
            var sourceGrids = allGrids.Where(g => g.Map == sourceMapId).ToList();
            foreach (var grid in sourceGrids)
            {
                // Capture original coordinate BEFORE shifting
                var originalCoord = grid.Coord;
                
                // Calculate new coordinate in target map
                var targetCoord = new Coord(
                    grid.Coord.X + shift.X,
                    grid.Coord.Y + shift.Y);

                // Update grid to target map
                grid.Map = targetMapId;
                grid.Coord = targetCoord;
                await _gridRepository.SaveGridAsync(grid);

                // Copy zoom 0 tile using ORIGINAL source coord (before shift)
                var tile = await _tileService.GetTileAsync(sourceMapId, originalCoord, 0);
                if (tile != null && !string.IsNullOrEmpty(tile.File))
                {
                    await _tileService.SaveTileAsync(
                        targetMapId,
                        targetCoord,
                        0,
                        tile.File,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        tile.TenantId,
                        tile.FileSizeBytes);

                    // Mark this tile for zoom regeneration
                    tilesToRegenerate.Add((targetMapId, targetCoord));
                }
            }

            // Delete source map
            await _mapRepository.DeleteMapAsync(sourceMapId);

            _logger.LogInformation("Merged map {SourceMapId} into {TargetMapId} (shift: {ShiftX}, {ShiftY}) for tenant {TenantId}",
                sourceMapId, targetMapId, shift.X, shift.Y, tenantId);
            _updateNotificationService.NotifyMapMerge(sourceMapId, targetMapId, shift, tenantId);
            // The source map row is gone — tell viewers so it leaves their map selector.
            _updateNotificationService.NotifyMapDeleted(sourceMapId);
        }
        
        // Regenerate zoom levels for all moved tiles (like Go does in client.go:779-791)
        _logger.LogInformation("Regenerating zoom levels for {TileCount} merged tiles", tilesToRegenerate.Count);
        
        // Build set of parent coords that need processing at each zoom level
        var needProcess = new Dictionary<(Coord coord, int mapId), bool>();
        foreach (var (mapId, coord) in tilesToRegenerate)
        {
            needProcess[(coord.Parent(), mapId)] = true;
        }
        
        // Iteratively regenerate zoom levels 1-6
        for (int z = 1; z <= 6; z++)
        {
            var process = needProcess.Keys.ToList();
            needProcess.Clear();

            foreach (var (coord, mapId) in process)
            {
                // Get tenantId from map
                var map = await _mapRepository.GetMapAsync(mapId);
                if (map == null)
                {
                    throw new InvalidOperationException($"Map {mapId} not found during zoom level regeneration");
                }

                await _tileService.UpdateZoomLevelAsync(mapId, coord, z, map.TenantId, gridStorage);
                needProcess[(coord.Parent(), mapId)] = true;
            }
        }
        
        _logger.LogInformation("Map merge complete: regenerated zoom levels 1-6");
    }

    public async Task<(int mapId, Coord coord)?> LocateGridAsync(string gridId)
    {
        var grid = await _gridRepository.GetGridAsync(gridId);
        if (grid == null)
            return null;

        return (grid.Map, grid.Coord);
    }

    public async Task DeleteMapTileAsync(int mapId, Coord coord, string gridStorage)
    {
        var grids = await _gridRepository.GetAllGridsAsync();
        var toDelete = grids.Where(g => g.Coord == coord && g.Map == mapId).ToList();

        foreach (var grid in toDelete)
        {
            await _gridRepository.DeleteGridAsync(grid.Id);
        }

        // Get tenantId from map
        var map = await _mapRepository.GetMapAsync(mapId);
        if (map == null)
        {
            throw new InvalidOperationException($"Map {mapId} not found during tile deletion");
        }

        await _tileService.SaveTileAsync(mapId, coord, 0, "", -1, map.TenantId, 0);

        // Update zoom levels
        var c = coord;
        for (int z = 1; z <= 6; z++)
        {
            c = c.Parent();
            await _tileService.UpdateZoomLevelAsync(mapId, c, z, map.TenantId, gridStorage);
        }
    }

    public async Task SetCoordinatesAsync(int mapId, Coord fromCoord, Coord toCoord, string gridStorage)
    {
        var diff = new Coord(toCoord.X - fromCoord.X, toCoord.Y - fromCoord.Y);
        var allGrids = await _gridRepository.GetAllGridsAsync();
        var mapGrids = allGrids.Where(g => g.Map == mapId).ToList();

        var tilesToUpdate = new List<TileData>();

        foreach (var grid in mapGrids)
        {
            grid.Coord = new Coord(grid.Coord.X + diff.X, grid.Coord.Y + diff.Y);
            await _gridRepository.SaveGridAsync(grid);
        }

        // Rebuild tiles at new coordinates
        foreach (var grid in mapGrids)
        {
            var tile = await _tileService.GetTileAsync(mapId, grid.Coord, 0);
            if (tile != null)
            {
                tilesToUpdate.Add(tile);
            }
        }

        // Update zoom levels
        var needProcess = new Dictionary<(Coord, int), bool>();
        foreach (var tile in tilesToUpdate)
        {
            await _tileService.SaveTileAsync(
                tile.MapId,
                tile.Coord,
                tile.Zoom,
                tile.File,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tile.TenantId,
                tile.FileSizeBytes);

            needProcess[(tile.Coord.Parent(), tile.MapId)] = true;
        }

        // Get tenantId from map
        var map = await _mapRepository.GetMapAsync(mapId);
        if (map == null)
        {
            throw new InvalidOperationException($"Map {mapId} not found during zoom level rebuild");
        }

        for (int z = 1; z <= 6; z++)
        {
            var process = needProcess.Keys.ToList();
            needProcess.Clear();

            foreach (var (coord, mid) in process)
            {
                await _tileService.UpdateZoomLevelAsync(mid, coord, z, map.TenantId, gridStorage);
                needProcess[(coord.Parent(), mid)] = true;
            }
        }
    }

    public async Task<int> CleanupMultiIdGridsAsync(string gridStorage)
    {
        var allGrids = await _gridRepository.GetAllGridsAsync();
        var groupedByMapCoord = allGrids
            .GroupBy(g => (g.Map, g.Coord.X, g.Coord.Y))
            .Where(g => g.Count() > 1)
            .ToList();

        int deletedCount = 0;

        foreach (var group in groupedByMapCoord)
        {
            var gridsToDelete = group.Skip(1).ToList();
            foreach (var grid in gridsToDelete)
            {
                await _gridRepository.DeleteGridAsync(grid.Id);
                deletedCount++;
            }

            // Rebuild this tile
            var coord = new Coord(group.Key.X, group.Key.Y);
            await DeleteMapTileAsync(group.Key.Map, coord, gridStorage);
        }

        return deletedCount;
    }
}
