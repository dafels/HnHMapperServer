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
    private readonly ZoomTileQueueService _zoomTileQueue;
    private readonly ITenantFilePathService _filePaths;
    private readonly MergeRejectionCache _mergeRejections;
    private readonly TileCacheService _tileCache;
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
        ZoomTileQueueService zoomTileQueue,
        ITenantFilePathService filePaths,
        MergeRejectionCache mergeRejections,
        TileCacheService tileCache,
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
        _zoomTileQueue = zoomTileQueue;
        _filePaths = filePaths;
        _mergeRejections = mergeRejections;
        _tileCache = tileCache;
        _logger = logger;
    }

    /// <summary>
    /// One 400x400 WebP tile at server zoom 0 covers 4x4 base grid cells.
    /// </summary>
    private const int WebpCellSize = 4;

    /// <summary>
    /// Cap on per-operation WebP regeneration enqueues. The queue is bounded (DropOldest), so a
    /// huge merge must not flush out upload-driven requests; anything beyond the cap (or beyond
    /// <see cref="ZoomTileQueueService.BulkEnqueueDepthLimit"/>) is covered by the DirtyWebpCells
    /// marks the tile saves wrote (the 5-minute backstop re-enqueues them).
    /// </summary>
    private const int MaxWebpEnqueuesPerOperation = 1500;

    /// <summary>
    /// Enqueues WebP chain regeneration for the distinct 4x4 cells covering the given base coords.
    /// The processor force-regenerates z0-z6, bumps mapRevision and invalidates the Web cache.
    /// </summary>
    private void EnqueueWebpRegenerationForCells(string tenantId, int mapId, IEnumerable<Coord> baseCoords)
    {
        var cells = new HashSet<(int X, int Y)>();
        foreach (var coord in baseCoords)
        {
            cells.Add(((int)Math.Floor(coord.X / (double)WebpCellSize), (int)Math.Floor(coord.Y / (double)WebpCellSize)));
        }

        var enqueued = 0;
        foreach (var cell in cells)
        {
            // Both limits: the per-operation cap, and the shared queue headroom every bulk
            // producer respects — 1500 merge cells on top of a backstop-filled queue would push
            // the channel past capacity and DropOldest would evict queued work.
            if (enqueued >= MaxWebpEnqueuesPerOperation
                || _zoomTileQueue.PendingCount >= ZoomTileQueueService.BulkEnqueueDepthLimit)
            {
                _logger.LogWarning(
                    "WebP regeneration enqueue stopped at {Enqueued} of {Total} cells for map {MapId} (cap {Cap}, queue depth {Depth}); " +
                    "the DirtyWebpCells backstop catches up on the rest",
                    enqueued, cells.Count, mapId, MaxWebpEnqueuesPerOperation, _zoomTileQueue.PendingCount);
                break;
            }

            _zoomTileQueue.EnqueueZoomRegeneration(
                new ZoomTileRequest(tenantId, mapId, cell.X * WebpCellSize, cell.Y * WebpCellSize));
            enqueued++;
        }
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

    /// <summary>
    /// Cells of the matrix that are holes: placeholders, and ids that are not a plain file name.
    /// Grid ids become pool file names (tenants/{t}/grids/{id}.png), so an id like "../5/0/1_1"
    /// would let an upload, a repair or a map delete reach another map's file.
    /// </summary>
    private static bool IsHoleGridId(string? gridId) =>
        IsPlaceholderGridId(gridId) || !TenantFilePathService.IsSafeGridId(gridId);

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

        // First pass: find which maps these grids belong to. Placeholder cells and unsafe ids are
        // holes — they never anchor and are never inserted.
        var unsafeIds = 0;
        for (int x = 0; x < gridUpdate.Grids.Count; x++)
        {
            for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
            {
                var gridId = gridUpdate.Grids[x][y];
                if (IsHoleGridId(gridId))
                {
                    if (!IsPlaceholderGridId(gridId))
                        unsafeIds++;
                    continue;
                }

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

        if (unsafeIds > 0)
        {
            _logger.LogWarning(
                "GridUpdate for tenant {TenantId}: ignored {Count} grid id(s) that are not plain file names",
                _tenantContext.GetRequiredTenantId(), unsafeIds);
        }

        // All known grids of one map must imply the same matrix offset. A contradiction means the
        // matrix mixes coordinate frames (client glitch or already-corrupted anchors) — writing
        // anything would spread the corruption, so the whole update is rejected.
        if (anchors.Any(kv => kv.Value.Offsets.Count > 1))
        {
            _logger.LogWarning(
                "GridUpdate REJECTED for tenant {TenantId}: conflicting offsets within one matrix. Matrix={Matrix}. Anchors={Anchors}",
                _tenantContext.GetRequiredTenantId(), DescribeMatrix(gridUpdate), DescribeAnchors(anchors));
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
                    if (IsHoleGridId(gridId))
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

        _logger.LogDebug("Client in map {MapId}", targetMapId);

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

        // A merge that would stack a source grid onto a target cell owned by a different grid id
        // means this matrix mixes two coordinate frames whose own witnesses each agree — the one
        // case the offset guard above cannot see. Merges are the only Grids writer without an
        // occupied-cell check and they delete the source map, so the whole update is rejected
        // here, before the insert loop plants anything (a merge-time refusal would come after the
        // matrix's unknown grids were already written in the target frame). In a coherent world a
        // legitimate merge never double-claims: a world cell has one grid id per tenant.
        // The verdict is a property of the two frames, not of this matrix, and a rejection changes
        // nothing — so it is remembered per (target, sources with their shifts) instead of being
        // recomputed, with both map loads and the full warning, on every gridUpdate of every
        // client in the area (MergeRejectionCache).
        var mergeEligible = ComputeMergeEligibility(anchors);
        if (mergeEligible.ContainsKey(targetMapId) && mergeEligible.Count > 1)
        {
            var tenantId = _tenantContext.GetRequiredTenantId();
            var rejectionKey = MergeRejectionCache.KeyOf(tenantId, targetMapId, mergeEligible
                .Where(kv => kv.Key != targetMapId)
                .Select(kv => (kv.Key, offset.X - kv.Value.X, offset.Y - kv.Value.Y)));
            if (_mergeRejections.IsRejected(rejectionKey, DateTime.UtcNow))
            {
                _logger.LogDebug(
                    "GridUpdate rejected for tenant {TenantId}: merge into {TargetMapId} was rejected as a double claim within the last {Minutes} min",
                    tenantId, targetMapId, MergeRejectionCache.Ttl.TotalMinutes);
                return EmptyResponse();
            }

            var doubleClaims = await FindMergeDoubleClaimsAsync(
                gridUpdate, mergeEligible, targetMapId, offset, knownByPos, ownerByCell);
            if (doubleClaims.Count > 0)
            {
                var repeats = _mergeRejections.Remember(rejectionKey, DateTime.UtcNow);
                _logger.LogWarning(
                    "GridUpdate REJECTED for tenant {TenantId}: merging map(s) {SourceMapIds} into {TargetMapId} would double-claim {Count} cell(s), e.g. {Samples}. " +
                    "{Repeats} identical rejection(s) since the last warning; the same merge is refused without re-checking for {Minutes} min. Matrix={Matrix}. Anchors={Anchors}",
                    tenantId,
                    string.Join(",", mergeEligible.Keys.Where(id => id != targetMapId)),
                    targetMapId, doubleClaims.Count, string.Join("; ", doubleClaims.Take(5)),
                    repeats, MergeRejectionCache.Ttl.TotalMinutes,
                    DescribeMatrix(gridUpdate), DescribeAnchors(anchors));
                return EmptyResponse();
            }

            _mergeRejections.Forget(rejectionKey);
        }

        // Process grids
        for (int x = 0; x < gridUpdate.Grids.Count; x++)
        {
            for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
            {
                var gridId = gridUpdate.Grids[x][y];
                if (IsHoleGridId(gridId))
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

        _logger.LogDebug("GridUpdate response (existing map): {GridCount} grids requested for map {MapId}, coords {Coords}",
            response.GridRequests.Count, response.Map, response.Coords);

        // Handle map merging if multiple maps detected — but only between maps that are each
        // witnessed by at least MIN_MERGE_WITNESSES agreeing grids in THIS matrix. Merges delete
        // the source map, so a single (possibly misplaced) witness is not enough evidence; a
        // deferred merge simply lands on a later matrix with more witnesses.
        if (anchors.Count > 1)
        {
            foreach (var (mapId, witness) in anchors.Where(kv => !mergeEligible.ContainsKey(kv.Key)))
            {
                _logger.LogWarning(
                    "Map merge deferred for tenant {TenantId}: map {MapId} witnessed by only {Count} grid(s) in this matrix (need >= {Min} agreeing); maps in matrix: {AllMapIds}",
                    _tenantContext.GetRequiredTenantId(), mapId, witness.Grids.Count, MIN_MERGE_WITNESSES,
                    string.Join(",", anchors.Keys));
            }

            if (mergeEligible.ContainsKey(targetMapId) && mergeEligible.Count > 1)
            {
                _logger.LogInformation("Merging {MapCount} maps into target map {TargetMapId}", mergeEligible.Count, targetMapId);
                await MergeMapsAsync(mergeEligible, targetMapId, offset, gridStorage);
            }
        }

        return response;
    }

    /// <summary>
    /// Maps that may take part in a merge: witnessed by at least MIN_MERGE_WITNESSES agreeing
    /// grids in this matrix, with their (single, guard-checked) offset.
    /// </summary>
    private static Dictionary<int, (int X, int Y)> ComputeMergeEligibility(Dictionary<int, MapWitness> anchors)
    {
        var eligible = new Dictionary<int, (int X, int Y)>();
        foreach (var (mapId, witness) in anchors)
        {
            if (witness.Grids.Count >= MIN_MERGE_WITNESSES)
            {
                var single = witness.Offsets.Single();
                eligible[mapId] = (single.X, single.Y);
            }
        }
        return eligible;
    }

    /// <summary>
    /// The target cells a merge of <paramref name="eligible"/> would claim twice, as
    /// "grid {id} -> (x,y) owned by {owner}" samples. Owners are the target's stored grids, the
    /// matrix's unknown grids the insert loop is about to plant (same skip rule as the loop), and
    /// the grids of sources checked before; a source's own pre-existing duplicates (corrupted DBs)
    /// are not counted against itself.
    /// </summary>
    private async Task<List<string>> FindMergeDoubleClaimsAsync(
        GridUpdateDto gridUpdate,
        Dictionary<int, (int X, int Y)> eligible,
        int targetMapId,
        (int X, int Y) targetOffset,
        Dictionary<(int X, int Y), GridData> knownByPos,
        Dictionary<Coord, string> matrixAreaOwners)
    {
        var moves = new List<(int SourceMapId, List<(string GridId, Coord Dest)> Grids)>();
        foreach (var (sourceMapId, sourceOffset) in eligible.OrderBy(kv => kv.Key))
        {
            if (sourceMapId == targetMapId)
                continue;

            var shift = new Coord(targetOffset.X - sourceOffset.X, targetOffset.Y - sourceOffset.Y);
            var sourceGrids = await _gridRepository.GetGridsByMapAsync(sourceMapId);
            moves.Add((sourceMapId, sourceGrids
                .Select(g => (g.Id, new Coord(g.Coord.X + shift.X, g.Coord.Y + shift.Y)))
                .ToList()));
        }

        var dests = moves.SelectMany(m => m.Grids).Select(g => g.Dest).ToList();
        if (dests.Count == 0)
            return new List<string>();

        var owners = new Dictionary<Coord, string>();
        foreach (var grid in await _gridRepository.GetGridsByMapInAreaAsync(targetMapId,
                     dests.Min(d => d.X), dests.Min(d => d.Y), dests.Max(d => d.X), dests.Max(d => d.Y)))
        {
            owners.TryAdd(grid.Coord, grid.Id);
        }

        for (int x = 0; x < gridUpdate.Grids.Count; x++)
        {
            for (int y = 0; y < gridUpdate.Grids[x].Count; y++)
            {
                var gridId = gridUpdate.Grids[x][y];
                if (IsHoleGridId(gridId) || knownByPos.ContainsKey((x, y)))
                    continue;

                var dest = new Coord(x + targetOffset.X, y + targetOffset.Y);
                if (matrixAreaOwners.TryGetValue(dest, out var occupant) && occupant != gridId)
                    continue; // the insert loop skips it

                owners.TryAdd(dest, gridId);
            }
        }

        var conflicts = new List<string>();
        foreach (var (sourceMapId, grids) in moves)
        {
            foreach (var (gridId, dest) in grids)
            {
                if (owners.TryGetValue(dest, out var owner) && owner != gridId)
                {
                    conflicts.Add($"map {sourceMapId} grid {gridId} -> ({dest.X},{dest.Y}) owned by {owner}");
                }
            }

            foreach (var (gridId, dest) in grids)
            {
                owners.TryAdd(dest, gridId);
            }
        }

        return conflicts;
    }

    private static string DescribeMatrix(GridUpdateDto gridUpdate) =>
        "[" + string.Join(",", gridUpdate.Grids.Select(row => "[" + string.Join(",", row) + "]")) + "]";

    private static string DescribeAnchors(Dictionary<int, MapWitness> anchors) =>
        string.Join("; ", anchors.SelectMany(kv => kv.Value.Grids.Select(g =>
            $"{g.GridId}@matrix({g.X},{g.Y}) -> map {kv.Key} stored({g.Stored.X},{g.Stored.Y}) offset({g.Stored.X - g.X},{g.Stored.Y - g.Y})")));

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
            // A corrupted DB can hold several grids on one cell. Computed before the loop shifts
            // any coordinate; on such a cell the coord-keyed per-map file is not re-homed.
            var contestedCells = sourceGrids
                .GroupBy(g => g.Coord)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();
            var rehomeCounts = new Dictionary<RehomeOutcome, int>();
            long rehomedBytes = 0; // new pool files: charged with the retired directory's credit
            foreach (var grid in sourceGrids)
            {
                // Capture original coordinate BEFORE shifting
                var originalCoord = grid.Coord;
                
                // Calculate new coordinate in target map
                var targetCoord = new Coord(
                    grid.Coord.X + shift.X,
                    grid.Coord.Y + shift.Y);

                // Read the zoom 0 tile (ORIGINAL source coord, before shift) first: its re-home
                // outcome decides whether the moved grid must be re-requested from clients.
                var tile = await _tileService.GetTileAsync(sourceMapId, originalCoord, 0);
                (string File, int Size, RehomeOutcome Outcome)? rehome = null;
                if (tile != null && !string.IsNullOrEmpty(tile.File))
                {
                    rehome = RehomeIntoGridPool(gridStorage, tenantId, grid.Id, tile.File, tile.FileSizeBytes,
                        contestedCell: contestedCells.Contains(originalCoord));
                    rehomeCounts[rehome.Value.Outcome] = rehomeCounts.GetValueOrDefault(rehome.Value.Outcome) + 1;
                    if (rehome.Value.Outcome == RehomeOutcome.Rehomed)
                    {
                        rehomedBytes += rehome.Value.Size;
                    }
                }

                // Update grid to target map
                grid.Map = targetMapId;
                grid.Coord = targetCoord;
                if (rehome?.Outcome == RehomeOutcome.SourceMissing)
                {
                    // Nothing on disk to show for this cell: make the grid requestable so the
                    // next client that sees it re-uploads the imagery into the pool.
                    grid.NextUpdate = DateTime.UtcNow.AddMinutes(-1);
                }
                await _gridRepository.SaveGridAsync(grid);

                if (rehome is { } copied)
                {
                    await _tileService.SaveTileAsync(
                        targetMapId,
                        targetCoord,
                        0,
                        copied.File,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        tile!.TenantId,
                        copied.Size);

                    // Mark this tile for zoom regeneration
                    tilesToRegenerate.Add((targetMapId, targetCoord));
                }
            }

            LogRehomeSummary(sourceMapId, targetMapId, rehomeCounts);

            // The source map is retired: drop its tile rows (all zooms) and per-map tile
            // directories. Leaving the rows behind is what accumulated thousands of orphaned
            // zoom-0 rows per merge. The copied zoom-0 rows now point into the grid pool
            // (re-homed above), which is never touched here; a row that could not be re-homed
            // still points into a per-map directory, and the guard keeps that directory.
            // The quota moves by the net change: re-homed copies charged, the freed per-map
            // directory credited (the WebP pyramid is reconciled-only).
            await _tileService.DeleteTilesByMapAsync(sourceMapId);
            await _tileService.DeleteRetiredMapDirectoriesAsync(tenantId, sourceMapId, gridStorage, rehomedBytes);

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

        // The merge rewrote Tiles rows (source rows deleted, target rows saved): drop the page-load
        // token snapshot. Nothing else would — the ZoomQ batch it enqueues writes WebP files only.
        await _tileCache.InvalidateCacheAsync(tenantId);

        // The target map's existing WebP tiles were generated before the merge and don't contain
        // the merged-in imagery; the background scans only fill MISSING files, so without this
        // the merged area stays invisible at zoom levels whose files already existed.
        if (tilesToRegenerate.Count > 0)
        {
            EnqueueWebpRegenerationForCells(tenantId, targetMapId, tilesToRegenerate.Select(t => t.coord));
        }
    }

    /// <summary>What happened to one merged zoom-0 row's file (see <see cref="RehomeIntoGridPool"/>).</summary>
    private enum RehomeOutcome
    {
        /// <summary>Not a per-map zoom-0 path (pool row, legacy "grids/x.png") — copied as is.</summary>
        Unchanged,
        /// <summary>Copied into the grid pool; the row points at the copy.</summary>
        Rehomed,
        /// <summary>The grid already had a pool PNG; the row points at it, nothing was overwritten.</summary>
        PoolWon,
        /// <summary>The per-map file is already gone; the row keeps its path and size as the restore key.</summary>
        SourceMissing,
        /// <summary>The copy failed; the row keeps its path (the directory guard keeps the file).</summary>
        CopyFailed,
        /// <summary>Several source grids claim the cell; the row keeps its path (the directory guard keeps the file).</summary>
        Contested
    }

    /// <summary>
    /// .hmap and public-map imports write zoom-0 PNGs into the importing map's own directory
    /// (tenants/{t}/{mapId}/0/{x}_{y}.png), while client uploads use the grid-id-keyed pool
    /// (tenants/{t}/grids/{gridId}.png). A merge retires the source map's directory, so a moved
    /// row pointing into any per-map zoom-0 directory is first re-homed into the pool — the
    /// layout every other consumer (region wipe, orphan purge, tile generation) already handles.
    /// Copy, never move: a merge is not transactional, and a throw midway must leave the
    /// still-live source map intact. An existing pool PNG wins and is never overwritten (twin
    /// rows may share it); the copy goes through a temp file + no-overwrite rename, the same
    /// idiom as AtomicFile.WriteAllBytesAsync, so a concurrent upload is never clobbered.
    /// </summary>
    private (string File, int Size, RehomeOutcome Outcome) RehomeIntoGridPool(
        string gridStorage, string tenantId, string gridId, string file, int size, bool contestedCell)
    {
        // Stored paths carry the separator of the OS that wrote them.
        var seg = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length != 5
            || seg[0] != "tenants"
            || seg[1] != tenantId
            || !int.TryParse(seg[2], out _)
            || seg[3] != "0"
            || !seg[4].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return (file, size, RehomeOutcome.Unchanged);
        }

        if (contestedCell)
        {
            // The per-map file is coord-keyed and several grids claim this cell, so there is no
            // telling whose terrain it shows; every one of them would otherwise get a copy.
            // grids/{id}.png must be grid id's own terrain: copy into no pool file — the row keeps
            // its path and the directory guard keeps the file.
            return (file, size, RehomeOutcome.Contested);
        }

        string? tempPath = null;
        try
        {
            // Both throw UnauthorizedAccessException for an unsafe grid id (legacy rows predating
            // the gridUpdate check): CopyFailed, the row keeps its path.
            var poolRelative = _filePaths.GetGridRelativePath(tenantId, gridId);
            var poolFull = _filePaths.GetGridFilePath(tenantId, gridId, gridStorage); // traversal-validated
            if (File.Exists(poolFull))
            {
                return (poolRelative, (int)new FileInfo(poolFull).Length, RehomeOutcome.PoolWon);
            }

            var sourceFull = Path.Combine(gridStorage, seg[0], seg[1], seg[2], seg[3], seg[4]);
            if (!File.Exists(sourceFull))
            {
                return (file, size, RehomeOutcome.SourceMissing);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(poolFull)!);
            tempPath = $"{poolFull}.{Guid.NewGuid():N}.tmp";
            File.Copy(sourceFull, tempPath);
            try
            {
                File.Move(tempPath, poolFull, overwrite: false);
            }
            catch (IOException) when (File.Exists(poolFull))
            {
                // An upload of this grid landed meanwhile — it is newer than the import render.
                return (poolRelative, (int)new FileInfo(poolFull).Length, RehomeOutcome.PoolWon);
            }

            return (poolRelative, (int)new FileInfo(poolFull).Length, RehomeOutcome.Rehomed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Merge: could not copy {File} into the grid pool for grid {GridId} (tenant {TenantId}); the row keeps its path",
                file, gridId, tenantId);
            return (file, size, RehomeOutcome.CopyFailed);
        }
        finally
        {
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// One line per merged source map that had per-map (imported) zoom-0 rows; Warning when a
    /// row is left pointing at a missing file or kept its old path.
    /// </summary>
    private void LogRehomeSummary(int sourceMapId, int targetMapId, Dictionary<RehomeOutcome, int> counts)
    {
        var rehomed = counts.GetValueOrDefault(RehomeOutcome.Rehomed);
        var poolWon = counts.GetValueOrDefault(RehomeOutcome.PoolWon);
        var missing = counts.GetValueOrDefault(RehomeOutcome.SourceMissing);
        var failed = counts.GetValueOrDefault(RehomeOutcome.CopyFailed);
        var contested = counts.GetValueOrDefault(RehomeOutcome.Contested);
        if (rehomed + poolWon + missing + failed + contested == 0)
            return;

        _logger.Log(missing + failed + contested > 0 ? LogLevel.Warning : LogLevel.Information,
            "Merge of map {SourceMapId} into {TargetMapId}: {Rehomed} imported zoom-0 tile(s) re-homed into the grid pool, " +
            "{PoolWon} already pooled, {Missing} without a source file (rows kept for restore, grids re-requested), " +
            "{Failed} copy failure(s), {Contested} on contested cell(s) (rows kept as they were)",
            sourceMapId, targetMapId, rehomed, poolWon, missing, failed, contested);
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

        // Regenerate the WebP chain so the wipe is visible in the viewer (which serves only the
        // WebP pyramid); with no remaining sources the regeneration deletes the stale files.
        EnqueueWebpRegenerationForCells(map.TenantId, mapId, new[] { coord });
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
