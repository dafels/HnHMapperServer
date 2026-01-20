using System.Diagnostics;
using System.Threading.Channels;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Represents a rendered grid ready for I/O operations.
/// Used to pass data from producer (rendering) to consumer (saving).
/// </summary>
internal sealed record RenderedGrid(
    HmapGridData SourceGrid,
    GridData GridData,
    Image<Rgba32> TileImage,
    string RelativePath,
    string FullPath);

/// <summary>
/// Represents a rendered zoom tile ready for I/O operations.
/// Used in the parallel zoom generation pipeline.
/// </summary>
internal sealed record RenderedZoomTile(
    Coord Coord,
    Image<Rgba32> Image,
    byte[] PngBytes,
    string FullPath,
    TileData Metadata);

/// <summary>
/// Helper class to track import progress with timing and overall percentage
/// </summary>
internal class ImportProgressTracker
{
    private readonly IProgress<HmapImportProgress>? _progress;
    private readonly Stopwatch _stopwatch;
    private readonly Stopwatch _phaseStopwatch;
    private DateTime _lastReportTime = DateTime.MinValue;
    private int _lastReportedItem = 0;

    // Phase weights for overall progress calculation (must sum to 100)
    private const int PHASE_PARSE = 2;           // Phase 1: Parsing
    private const int PHASE_FETCH_TILES = 18;    // Phase 2: Fetching tiles
    private const int PHASE_IMPORT_GRIDS = 60;   // Phase 3: Importing grids (longest)
    private const int PHASE_ZOOM_LEVELS = 15;    // Phase 4: Generating zoom
    private const int PHASE_MARKERS = 5;         // Phase 5: Importing markers

    public int CurrentPhase { get; private set; }
    public const int TotalPhases = 5;

    private double _completedPhaseWeight = 0;
    private double _currentPhaseWeight = 0;

    public ImportProgressTracker(IProgress<HmapImportProgress>? progress)
    {
        _progress = progress;
        _stopwatch = Stopwatch.StartNew();
        _phaseStopwatch = new Stopwatch();
    }

    public void StartPhase(int phaseNumber, string phaseName)
    {
        CurrentPhase = phaseNumber;
        _phaseStopwatch.Restart();
        _lastReportedItem = 0;

        _currentPhaseWeight = phaseNumber switch
        {
            1 => PHASE_PARSE,
            2 => PHASE_FETCH_TILES,
            3 => PHASE_IMPORT_GRIDS,
            4 => PHASE_ZOOM_LEVELS,
            5 => PHASE_MARKERS,
            _ => 0
        };
    }

    public void CompletePhase()
    {
        _completedPhaseWeight += _currentPhaseWeight;
    }

    public void Report(string phase, int current, int total, string? itemName = null, bool forceReport = false)
    {
        // Throttle reports to max once every 100ms unless forced or significant progress
        var now = DateTime.UtcNow;
        var timeSinceLastReport = (now - _lastReportTime).TotalMilliseconds;
        var itemsSinceLastReport = current - _lastReportedItem;

        // Report if: forced, first item, last item, 100ms passed, or 1% progress made
        var percentProgress = total > 0 ? (double)itemsSinceLastReport / total * 100 : 0;
        if (!forceReport && current != 1 && current != total && timeSinceLastReport < 100 && percentProgress < 1)
        {
            return;
        }

        _lastReportTime = now;
        _lastReportedItem = current;

        // Calculate phase progress (0-1)
        var phaseProgress = total > 0 ? (double)current / total : 0;

        // Calculate overall progress
        var overallPercent = _completedPhaseWeight + (_currentPhaseWeight * phaseProgress);

        // Calculate speed
        var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
        var phaseElapsedSeconds = _phaseStopwatch.Elapsed.TotalSeconds;
        var itemsPerSecond = phaseElapsedSeconds > 0.5 ? current / phaseElapsedSeconds : 0;

        _progress?.Report(new HmapImportProgress
        {
            Phase = phase,
            CurrentItem = current,
            TotalItems = total,
            CurrentItemName = itemName ?? "",
            PhaseNumber = CurrentPhase,
            TotalPhases = TotalPhases,
            OverallPercent = Math.Min(overallPercent, 100),
            ElapsedSeconds = elapsedSeconds,
            ItemsPerSecond = Math.Round(itemsPerSecond, 1)
        });
    }
}

/// <summary>
/// Service for importing .hmap files into the map database
/// </summary>
public class HmapImportService : IHmapImportService
{
    private readonly IGridRepository _gridRepository;
    private readonly IMapRepository _mapRepository;
    private readonly ITileService _tileService;
    private readonly ITileRepository _tileRepository;
    private readonly IOverlayDataRepository _overlayRepository;
    private readonly IStorageQuotaService _quotaService;
    private readonly IMapNameService _mapNameService;
    private readonly IMarkerService _markerService;
    private readonly ILogger<HmapImportService> _logger;
    private const int GRID_SIZE = 100; // 100x100 tiles per grid

    // Security: Maximum HMAP file size (200 MB)
    private const long MaxHmapFileSizeBytes = 200 * 1024 * 1024;

    // Global lock to prevent concurrent imports across all tenants
    // Only one .hmap import can run at a time to prevent server overload
    private static readonly SemaphoreSlim _globalImportLock = new(1, 1);

    // Merge validation constants
    private const int PROXIMITY_THRESHOLD = 10;        // Manhattan distance for spatial proximity check
    private const int MIN_PROXIMATE_MATCHES = 5;       // Minimum Grid ID matches near merged area to trust offset
    private const double CAVE_OVERLAP_THRESHOLD = 50.0;  // % coord overlap to trigger cave detection
    private const double CAVE_CONTENT_THRESHOLD = 10.0;  // % content match below this = cave (different terrain)

    public HmapImportService(
        IGridRepository gridRepository,
        IMapRepository mapRepository,
        ITileService tileService,
        ITileRepository tileRepository,
        IOverlayDataRepository overlayRepository,
        IStorageQuotaService quotaService,
        IMapNameService mapNameService,
        IMarkerService markerService,
        ILogger<HmapImportService> logger)
    {
        _gridRepository = gridRepository;
        _mapRepository = mapRepository;
        _tileService = tileService;
        _tileRepository = tileRepository;
        _overlayRepository = overlayRepository;
        _quotaService = quotaService;
        _mapNameService = mapNameService;
        _markerService = markerService;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsImportInProgress() => _globalImportLock.CurrentCount == 0;

    public async Task<HmapImportResult> ImportAsync(
        Stream hmapStream,
        string tenantId,
        HmapImportMode mode,
        string gridStorage,
        IProgress<HmapImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new HmapImportResult();
        var tracker = new ImportProgressTracker(progress);

        // Try to acquire global import lock (only one import at a time across all tenants)
        if (!await _globalImportLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _logger.LogWarning("Import rejected for tenant {TenantId}: another import is already in progress", tenantId);
            result.Success = false;
            result.ErrorMessage = "Another import is already in progress. Please wait for it to complete and try again.";
            return result;
        }

        _logger.LogInformation("Acquired global import lock for tenant {TenantId}", tenantId);

        try
        {
            // Security: Check file size before processing
            if (hmapStream.CanSeek && hmapStream.Length > MaxHmapFileSizeBytes)
            {
                _logger.LogWarning(
                    "Security: HMAP import rejected for tenant {TenantId}. Reason: FileTooLarge, Size: {Size} bytes, Limit: {Limit} bytes",
                    tenantId, hmapStream.Length, MaxHmapFileSizeBytes);

                result.Success = false;
                result.ErrorMessage = $"HMAP file exceeds maximum size limit of {MaxHmapFileSizeBytes / 1024 / 1024} MB. " +
                                      $"Uploaded file is {hmapStream.Length / 1024 / 1024} MB.";
                return result;
            }

            // Phase 1: Parse .hmap file
            cancellationToken.ThrowIfCancellationRequested();
            tracker.StartPhase(1, "Parsing");
            tracker.Report("Parsing .hmap file", 0, 1, "Reading file...", forceReport: true);

            var reader = new HmapReader();
            var hmapData = reader.Read(hmapStream);
            tracker.Report("Parsing .hmap file", 1, 1, $"Found {hmapData.Grids.Count} grids", forceReport: true);
            tracker.CompletePhase();

            _logger.LogInformation("Parsed .hmap: {GridCount} grids, {SegmentCount} segments",
                hmapData.Grids.Count, hmapData.GetSegmentIds().Count());

            // Filter to only the 3 largest segments by grid count
            cancellationToken.ThrowIfCancellationRequested();
            var allSegments = hmapData.GetSegmentIds()
                .Select(id => new { Id = id, GridCount = hmapData.GetGridsForSegment(id).Count })
                .OrderByDescending(s => s.GridCount)
                .ToList();

            const int MAX_SEGMENTS = 3;
            var segments = allSegments.Take(MAX_SEGMENTS).Select(s => s.Id).ToList();
            var skippedSegments = allSegments.Skip(MAX_SEGMENTS).ToList();

            if (skippedSegments.Count > 0)
            {
                _logger.LogInformation(
                    "Skipping {SkippedCount} smaller segments (keeping top {MaxSegments} by grid count). Skipped: {SkippedDetails}",
                    skippedSegments.Count,
                    MAX_SEGMENTS,
                    string.Join(", ", skippedSegments.Select(s => $"{s.Id:X}({s.GridCount} grids)")));
            }

            // Get grids only for segments we're importing
            var gridsToImport = segments
                .SelectMany(id => hmapData.GetGridsForSegment(id))
                .ToList();

            _logger.LogInformation("Will import {GridCount} grids from {SegmentCount} segments",
                gridsToImport.Count, segments.Count);

            // Phase 2: Fetch tile resources from Haven server
            cancellationToken.ThrowIfCancellationRequested();
            var allResources = gridsToImport
                .SelectMany(g => g.Tilesets.Select(t => t.ResourceName))
                .Distinct()
                .ToList();

            tracker.StartPhase(2, "Fetching tiles");
            tracker.Report("Fetching tile resources", 0, allResources.Count, "Connecting to Haven...", forceReport: true);

            var tileCacheDir = Path.Combine(gridStorage, "hmap-tile-cache");
            using var tileResourceService = new TileResourceService(tileCacheDir);

            var fetchProgress = new Progress<(int current, int total, string name)>(p =>
            {
                tracker.Report("Fetching tile resources", p.current, p.total, p.name);
            });

            await tileResourceService.PrefetchTilesAsync(allResources, fetchProgress);
            tracker.CompletePhase();

            // Check for network errors during tile fetching
            var networkError = tileResourceService.GetFirstNetworkError();
            if (networkError != null)
            {
                _logger.LogWarning("Tile fetch warning: {NetworkError}", networkError);
            }

            // ===== MERGE VALIDATION (only in Merge mode) =====
            // Calculate per-segment offsets and validate which segments should merge vs create new maps
            var segmentMergeDecisions = new Dictionary<long, (bool shouldMerge, int? targetMapId, int offsetX, int offsetY, string reason)>();

            if (mode == HmapImportMode.Merge)
            {
                // Build lookup tables from existing grids in tenant
                var existingGrids = await _gridRepository.GetAllGridsAsync();

                // Unique Grid ID -> Coord (only grids that appear once - unique content)
                var uniqueGridById = existingGrids
                    .GroupBy(g => g.Id)
                    .Where(grp => grp.Count() == 1)
                    .ToDictionary(grp => grp.Key, grp => grp.First());

                // MapId -> (Coord -> Grid ID) for cave detection per target map
                var gridIdByMapAndCoord = existingGrids
                    .GroupBy(g => g.Map)
                    .ToDictionary(
                        mapGrp => mapGrp.Key,
                        mapGrp => mapGrp.ToDictionary(g => g.Coord, g => g.Id));

                _logger.LogInformation("Merge validation: {TotalGrids} existing grids, {UniqueGrids} unique Grid IDs",
                    existingGrids.Count, uniqueGridById.Count);

                // Calculate per-segment offsets
                var segmentOffsets = new Dictionary<long, (int offsetX, int offsetY, int matchCount, List<HmapGridData> grids, int? targetMapId)>();

                foreach (var segmentId in segments)
                {
                    var segmentGrids = hmapData.GetGridsForSegment(segmentId);

                    // Find unique Grid ID matches between file and DB
                    var matches = segmentGrids
                        .Where(g => uniqueGridById.ContainsKey(g.GridIdString))
                        .Select(g => (
                            FileGrid: g,
                            DbGrid: uniqueGridById[g.GridIdString],
                            OffsetX: uniqueGridById[g.GridIdString].Coord.X - g.TileX,
                            OffsetY: uniqueGridById[g.GridIdString].Coord.Y - g.TileY
                        ))
                        .ToList();

                    if (matches.Count == 0)
                    {
                        // No Grid ID matches - will create new map
                        segmentMergeDecisions[segmentId] = (false, null, 0, 0, "no Grid ID matches");
                        _logger.LogInformation("Segment {SegId:X}: No Grid ID matches - will create new map", segmentId);
                        continue;
                    }

                    // Group by offset, find dominant
                    var offsetGroups = matches
                        .GroupBy(m => (m.OffsetX, m.OffsetY))
                        .OrderByDescending(g => g.Count())
                        .ToList();

                    var (offsetX, offsetY) = offsetGroups.First().Key;
                    var matchCount = offsetGroups.First().Count();
                    var targetMapId = matches.First().DbGrid.Map;

                    segmentOffsets[segmentId] = (offsetX, offsetY, matchCount, segmentGrids, targetMapId);

                    _logger.LogInformation("Segment {SegId:X}: {MatchCount} Grid ID matches, offset ({OffsetX},{OffsetY}), target map {MapId}",
                        segmentId, matchCount, offsetX, offsetY, targetMapId);
                }

                // Validate segments with offsets
                if (segmentOffsets.Count > 0)
                {
                    // Start with dominant segment (most matches)
                    var dominant = segmentOffsets.OrderByDescending(s => s.Value.matchCount).First();
                    var mergedCoords = new HashSet<(int, int)>();

                    // Dominant segment always merges
                    foreach (var grid in dominant.Value.grids)
                        mergedCoords.Add((grid.TileX + dominant.Value.offsetX, grid.TileY + dominant.Value.offsetY));

                    segmentMergeDecisions[dominant.Key] = (true, dominant.Value.targetMapId, dominant.Value.offsetX, dominant.Value.offsetY,
                        $"{dominant.Value.matchCount} Grid ID matches (dominant)");

                    _logger.LogInformation("Segment {SegId:X}: MERGE (dominant) - {MatchCount} Grid ID matches",
                        dominant.Key, dominant.Value.matchCount);

                    // Validate other segments
                    foreach (var (segId, offset) in segmentOffsets.Where(s => s.Key != dominant.Key))
                    {
                        // SPATIAL PROXIMITY CHECK
                        int proximateMatches = 0;
                        foreach (var grid in offset.grids)
                        {
                            if (uniqueGridById.TryGetValue(grid.GridIdString, out var dbGrid))
                            {
                                bool isProximate = mergedCoords.Any(mc =>
                                    Math.Abs(dbGrid.Coord.X - mc.Item1) + Math.Abs(dbGrid.Coord.Y - mc.Item2) <= PROXIMITY_THRESHOLD);
                                if (isProximate)
                                    proximateMatches++;
                            }
                        }

                        if (proximateMatches < MIN_PROXIMATE_MATCHES)
                        {
                            segmentMergeDecisions[segId] = (false, null, 0, 0,
                                $"not proximate: only {proximateMatches}/{MIN_PROXIMATE_MATCHES} matches near merged area");
                            _logger.LogInformation("Segment {SegId:X}: NEW MAP (not proximate) - only {Count} matches near merged area",
                                segId, proximateMatches);
                            continue;
                        }

                        // CAVE DETECTION - check against target map's grids only
                        int coordOverlapCount = 0, contentMatchCount = 0;
                        if (offset.targetMapId.HasValue && gridIdByMapAndCoord.TryGetValue(offset.targetMapId.Value, out var targetMapCoords))
                        {
                            foreach (var grid in offset.grids)
                            {
                                var adjustedCoord = new Coord(grid.TileX + offset.offsetX, grid.TileY + offset.offsetY);
                                if (targetMapCoords.TryGetValue(adjustedCoord, out var dbGridId))
                                {
                                    coordOverlapCount++;
                                    if (grid.GridIdString == dbGridId)
                                        contentMatchCount++;
                                }
                            }
                        }

                        double coordOverlapPct = offset.grids.Count > 0 ? (double)coordOverlapCount / offset.grids.Count * 100 : 0;
                        double contentMatchRate = coordOverlapCount > 0 ? (double)contentMatchCount / coordOverlapCount * 100 : 0;

                        if (coordOverlapPct >= CAVE_OVERLAP_THRESHOLD && contentMatchRate < CAVE_CONTENT_THRESHOLD)
                        {
                            segmentMergeDecisions[segId] = (false, null, 0, 0,
                                $"cave detected: {coordOverlapPct:F0}% overlap, {contentMatchRate:F0}% content match");
                            _logger.LogInformation("Segment {SegId:X}: NEW MAP (cave) - {Overlap:F0}% coord overlap, {Content:F0}% content match",
                                segId, coordOverlapPct, contentMatchRate);
                            continue;
                        }

                        // Passed validation - will merge
                        segmentMergeDecisions[segId] = (true, offset.targetMapId, offset.offsetX, offset.offsetY,
                            $"{offset.matchCount} Grid ID matches, {proximateMatches} proximate");

                        foreach (var grid in offset.grids)
                            mergedCoords.Add((grid.TileX + offset.offsetX, grid.TileY + offset.offsetY));

                        _logger.LogInformation("Segment {SegId:X}: MERGE - {MatchCount} Grid ID matches, {Proximate} proximate",
                            segId, offset.matchCount, proximateMatches);
                    }
                }

                // Clear large collections to reduce GC pressure during grid import
                uniqueGridById.Clear();
                gridIdByMapAndCoord.Clear();
                segmentOffsets.Clear();
            }

            // Phase 3: Import grids from each segment
            tracker.StartPhase(3, "Importing grids");
            var totalGridsToProcess = gridsToImport.Count;
            var processedGridsSoFar = 0;

            foreach (var segmentId in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentGrids = hmapData.GetGridsForSegment(segmentId);

                // Get pre-calculated merge decision (if available)
                (bool shouldMerge, int? targetMapId, int offsetX, int offsetY, string reason)? mergeDecision = null;
                if (segmentMergeDecisions.TryGetValue(segmentId, out var decision))
                {
                    mergeDecision = decision;
                }

                var (mapId, isNewMap, gridsImported, gridsSkipped, createdGridIds, gridsProcessed) = await ImportSegmentAsync(
                    segmentId, segmentGrids, tenantId, mode, gridStorage, tileResourceService,
                    tracker, processedGridsSoFar, totalGridsToProcess, cancellationToken, mergeDecision);

                processedGridsSoFar += gridsProcessed;

                if (mapId > 0)
                {
                    result.AffectedMapIds.Add(mapId);
                    if (isNewMap)
                    {
                        result.CreatedMapIds.Add(mapId);
                        if (gridsImported > 0)
                        {
                            result.MapsCreated++;
                            // Track reason for new map creation
                            if (mergeDecision.HasValue && !mergeDecision.Value.shouldMerge)
                            {
                                if (mergeDecision.Value.reason.Contains("cave"))
                                    result.CavesAsNewMaps++;
                                else if (mergeDecision.Value.reason.Contains("not proximate"))
                                    result.NotProximateAsNewMaps++;
                            }
                        }
                    }
                    else if (gridsImported > 0)
                    {
                        // Grids were merged into an existing map
                        result.GridsMerged += gridsImported;
                    }
                }

                result.CreatedGridIds.AddRange(createdGridIds);
                result.GridsImported += gridsImported;
                result.GridsSkipped += gridsSkipped;
                result.TilesRendered += gridsImported;
            }

            tracker.CompletePhase();

            // Phase 4: Generate zoom levels for affected maps (optimized with parallel processing and caching)
            tracker.StartPhase(4, "Generating zoom levels");
            var distinctMaps = result.AffectedMapIds.Distinct().ToList();
            int mapIndex = 0;
            foreach (var mapId in distinctMaps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mapIndex++;
                await GenerateZoomLevelsForMapAsync(mapId, tenantId, gridStorage, tracker, mapIndex, distinctMaps.Count, cancellationToken);
            }
            tracker.CompletePhase();

            // Phase 5: Import markers (batched for performance)
            if (hmapData.Markers.Count > 0)
            {
                tracker.StartPhase(5, "Importing markers");
                var totalMarkers = hmapData.Markers.Count;
                const int MARKER_BATCH_SIZE = 500;

                // Collect all valid markers across all segments
                var markerBatch = new List<(string GridId, int X, int Y, string Name, string Image)>(MARKER_BATCH_SIZE);
                var processedCount = 0;

                foreach (var segmentId in segments)
                {
                    var segmentMarkers = hmapData.GetMarkersForSegment(segmentId);
                    var segmentGrids = hmapData.GetGridsForSegment(segmentId);

                    // Build lookup: (GridTileX, GridTileY) -> GridId
                    var gridLookup = segmentGrids.ToDictionary(
                        g => (g.TileX, g.TileY),
                        g => g.GridIdString
                    );

                    foreach (var marker in segmentMarkers)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        processedCount++;

                        // Convert marker's absolute tile coords to grid coords
                        var markerGridX = marker.TileX / GRID_SIZE;
                        var markerGridY = marker.TileY / GRID_SIZE;

                        // Find the grid this marker belongs to
                        if (!gridLookup.TryGetValue((markerGridX, markerGridY), out var gridId))
                        {
                            result.MarkersSkipped++;
                            continue;
                        }

                        // Extract position within the grid (0-99)
                        var posX = marker.TileX % GRID_SIZE;
                        var posY = marker.TileY % GRID_SIZE;

                        // Determine image/icon based on marker type
                        var image = marker switch
                        {
                            HmapSMarker sm => sm.ResourceName,
                            _ => "gfx/terobjs/mm/custom"
                        };

                        markerBatch.Add((gridId, posX, posY, marker.Name, image));

                        // Flush batch when full
                        if (markerBatch.Count >= MARKER_BATCH_SIZE)
                        {
                            tracker.Report("Importing markers", processedCount, totalMarkers,
                                $"Saving batch of {markerBatch.Count} markers");

                            try
                            {
                                await _markerService.BulkUploadMarkersAsync(markerBatch);
                                result.MarkersImported += markerBatch.Count;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to import marker batch of {Count}", markerBatch.Count);
                                result.MarkersSkipped += markerBatch.Count;
                            }
                            markerBatch.Clear();
                        }
                    }
                }

                // Flush remaining markers
                if (markerBatch.Count > 0)
                {
                    tracker.Report("Importing markers", processedCount, totalMarkers,
                        $"Saving final batch of {markerBatch.Count} markers");

                    try
                    {
                        await _markerService.BulkUploadMarkersAsync(markerBatch);
                        result.MarkersImported += markerBatch.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import final marker batch of {Count}", markerBatch.Count);
                        result.MarkersSkipped += markerBatch.Count;
                    }
                    markerBatch.Clear();
                }

                _logger.LogInformation("Markers: {Imported} imported, {Skipped} skipped",
                    result.MarkersImported, result.MarkersSkipped);
                tracker.CompletePhase();
            }

            result.Success = true;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            _logger.LogInformation(
                "Import completed: {MapsCreated} maps ({GridsMerged} grids merged, {CavesAsNewMaps} caves, {NotProximate} not proximate), " +
                "{GridsImported} grids imported, {GridsSkipped} skipped, {MarkersImported} markers, {Duration}ms",
                result.MapsCreated, result.GridsMerged, result.CavesAsNewMaps, result.NotProximateAsNewMaps,
                result.GridsImported, result.GridsSkipped, result.MarkersImported, result.Duration.TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Import canceled for tenant {TenantId}", tenantId);
            result.Success = false;
            result.ErrorMessage = "Import was canceled";
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        finally
        {
            _globalImportLock.Release();
            _logger.LogInformation("Released global import lock for tenant {TenantId}", tenantId);
        }
    }

    public async Task CleanupFailedImportAsync(
        IEnumerable<int> mapIds,
        IEnumerable<string> gridIds,
        string tenantId,
        string gridStorage)
    {
        _logger.LogInformation("Cleaning up failed import for tenant {TenantId}: {MapCount} maps, {GridCount} grids",
            tenantId, mapIds.Count(), gridIds.Count());

        // Delete grids first (they may reference maps)
        foreach (var gridId in gridIds)
        {
            try
            {
                var grid = await _gridRepository.GetGridAsync(gridId);
                if (grid != null)
                {
                    await _gridRepository.DeleteGridAsync(gridId);
                    _logger.LogDebug("Deleted grid {GridId}", gridId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete grid {GridId} during cleanup", gridId);
            }
        }

        // Delete maps (only newly created ones)
        foreach (var mapId in mapIds)
        {
            try
            {
                // Delete map directory (includes all tile files)
                var mapDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString());
                long totalDeletedBytes = 0;

                if (Directory.Exists(mapDir))
                {
                    // Calculate total size for storage quota adjustment
                    foreach (var file in Directory.GetFiles(mapDir, "*.png", SearchOption.AllDirectories))
                    {
                        try
                        {
                            totalDeletedBytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // Ignore file access errors
                        }
                    }

                    try
                    {
                        Directory.Delete(mapDir, recursive: true);
                        _logger.LogDebug("Deleted map directory {MapDir}", mapDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete map directory {MapDir}", mapDir);
                    }
                }

                // Decrement storage quota
                if (totalDeletedBytes > 0)
                {
                    var sizeMB = totalDeletedBytes / (1024.0 * 1024.0);
                    await _quotaService.IncrementStorageUsageAsync(tenantId, -sizeMB);
                }

                // Delete all tile records for this map
                await _tileRepository.DeleteTilesByMapAsync(mapId);

                // Delete all overlay records for this map
                await _overlayRepository.DeleteByMapAsync(mapId);

                // Delete map record
                await _mapRepository.DeleteMapAsync(mapId);
                _logger.LogDebug("Deleted map {MapId}", mapId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete map {MapId} during cleanup", mapId);
            }
        }

        _logger.LogInformation("Cleanup completed for tenant {TenantId}", tenantId);
    }

    private async Task<(int mapId, bool isNewMap, int gridsImported, int gridsSkipped, List<string> createdGridIds, int gridsProcessed)> ImportSegmentAsync(
        long segmentId,
        List<HmapGridData> grids,
        string tenantId,
        HmapImportMode mode,
        string gridStorage,
        TileResourceService tileResourceService,
        ImportProgressTracker tracker,
        int processedSoFar,
        int totalGridsOverall,
        CancellationToken cancellationToken,
        (bool shouldMerge, int? targetMapId, int offsetX, int offsetY, string reason)? mergeDecision = null)
    {
        int mapId = 0;
        bool isNewMap = false;
        int gridsImported = 0;
        int gridsSkipped = 0;
        var createdGridIds = new List<string>();
        var segmentGridCount = grids.Count;

        // Coordinate offset for merging files with different coordinate systems
        int coordOffsetX = 0;
        int coordOffsetY = 0;

        // ===== STEP 1: Determine map and filter grids to import =====
        List<HmapGridData> gridsToImport;

        if (mode == HmapImportMode.CreateNew)
        {
            // Always create new map and import all grids
            // Deduplicate by GridIdString to prevent UNIQUE constraint violations if source file has duplicates
            mapId = await CreateNewMapAsync(tenantId);
            isNewMap = true;
            gridsToImport = grids.DistinctBy(g => g.GridIdString).ToList();
            gridsSkipped = grids.Count - gridsToImport.Count;
            _logger.LogInformation("Created new map {MapId} for segment {SegmentId:X}", mapId, segmentId);
        }
        else // Merge mode
        {
            // Use pre-calculated merge decision if available
            if (mergeDecision.HasValue)
            {
                var decision = mergeDecision.Value;

                if (decision.shouldMerge && decision.targetMapId.HasValue)
                {
                    // Merge into existing map with pre-calculated offset
                    mapId = decision.targetMapId.Value;
                    isNewMap = false;
                    coordOffsetX = decision.offsetX;
                    coordOffsetY = decision.offsetY;
                    _logger.LogInformation("Merging segment {SegmentId:X} into map {MapId} (offset {OffsetX},{OffsetY}) - {Reason}",
                        segmentId, mapId, coordOffsetX, coordOffsetY, decision.reason);
                }
                else
                {
                    // Create new map (failed validation: cave, not proximate, or no matches)
                    mapId = await CreateNewMapAsync(tenantId);
                    isNewMap = true;
                    // No coordinate offset for new maps
                    _logger.LogInformation("Created new map {MapId} for segment {SegmentId:X} - {Reason}",
                        mapId, segmentId, decision.reason);
                }

                // In merge mode with pre-calculated decision, filter out existing grids
                var allGridIds = grids.Select(g => g.GridIdString).ToList();
                var existingGridIds = await _gridRepository.GetExistingGridIdsAsync(allGridIds);
                // Filter out existing grids and deduplicate by GridIdString to prevent UNIQUE constraint violations
                // when the same grid ID appears multiple times in the source file
                gridsToImport = grids
                    .Where(g => !existingGridIds.Contains(g.GridIdString))
                    .DistinctBy(g => g.GridIdString)
                    .ToList();
                gridsSkipped = grids.Count - gridsToImport.Count;

                if (gridsSkipped > 0)
                {
                    _logger.LogInformation("Skipping {SkippedCount} existing grids in segment {SegmentId:X}",
                        gridsSkipped, segmentId);
                }
            }
            else
            {
                // Fallback: original merge logic (for backwards compatibility)
                // Batch check for existing grids (single query instead of N queries)
                var allGridIds = grids.Select(g => g.GridIdString).ToList();
                var existingGridIds = await _gridRepository.GetExistingGridIdsAsync(allGridIds);

                // Find existing map from any existing grid
                int? existingMapId = null;
                if (existingGridIds.Count > 0)
                {
                    var firstExistingId = existingGridIds.First();
                    var existingGrid = await _gridRepository.GetGridAsync(firstExistingId);
                    existingMapId = existingGrid?.Map;

                    // ===== COORDINATE OFFSET CALCULATION =====
                    // Different .hmap files can use different coordinate systems for the same physical area.
                    // Calculate the offset between existing grid coords and file grid coords.
                    if (existingGrid != null)
                    {
                        var fileGrid = grids.FirstOrDefault(g => g.GridIdString == firstExistingId);
                        if (fileGrid != null)
                        {
                            coordOffsetX = existingGrid.Coord.X - fileGrid.TileX;
                            coordOffsetY = existingGrid.Coord.Y - fileGrid.TileY;

                            if (coordOffsetX != 0 || coordOffsetY != 0)
                            {
                                _logger.LogInformation(
                                    "Detected coordinate offset for segment {SegmentId:X}: ({OffsetX}, {OffsetY})",
                                    segmentId, coordOffsetX, coordOffsetY);
                            }
                        }
                    }
                }

                if (existingMapId.HasValue)
                {
                    mapId = existingMapId.Value;
                    isNewMap = false;
                    _logger.LogInformation("Merging segment {SegmentId:X} into existing map {MapId}", segmentId, mapId);
                }
                else
                {
                    mapId = await CreateNewMapAsync(tenantId);
                    isNewMap = true;
                    _logger.LogInformation("Created new map {MapId} for segment {SegmentId:X} (no existing grids)", mapId, segmentId);
                }

                // Filter to only new grids and deduplicate by GridIdString to prevent UNIQUE constraint violations
                gridsToImport = grids
                    .Where(g => !existingGridIds.Contains(g.GridIdString))
                    .DistinctBy(g => g.GridIdString)
                    .ToList();
                gridsSkipped = grids.Count - gridsToImport.Count;

                if (gridsSkipped > 0)
                {
                    _logger.LogInformation("Skipping {SkippedCount} existing grids in segment {SegmentId:X}",
                        gridsSkipped, segmentId);
                }
            }
        }

        if (gridsToImport.Count == 0)
        {
            // Report progress for skipped grids
            tracker.Report("Importing grids", processedSoFar + segmentGridCount, totalGridsOverall,
                $"Segment {segmentId:X} - all grids exist", forceReport: true);
            return (mapId, isNewMap, 0, gridsSkipped, createdGridIds, segmentGridCount);
        }

        // ===== STEP 2: Producer-Consumer Pipeline =====
        // Producer: Parallel CPU rendering
        // Consumer: Sequential I/O and batched DB writes

        const int RENDER_PARALLELISM = 4; // CPU-bound rendering parallelism
        const int CHANNEL_CAPACITY = 50;  // Increased from 20 for better buffering
        const int BATCH_SIZE = 500;       // DB batch size

        var channel = Channel.CreateBounded<RenderedGrid>(new BoundedChannelOptions(CHANNEL_CAPACITY)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        using var batchContext = new BatchImportContext(BATCH_SIZE);
        var processedCount = 0;
        var importedGridIds = new List<string>();
        Exception? producerException = null;

        // Producer task: Parallel rendering with batched task processing
        var producerTask = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(RENDER_PARALLELISM);
            const int TASK_BATCH_SIZE = 100;  // Reduced from 500 for more frequent sync points
            var renderTasks = new List<Task>(TASK_BATCH_SIZE);

            try
            {
                foreach (var grid in gridsToImport)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(cancellationToken);

                    var renderTask = Task.Run(async () =>
                    {
                        try
                        {
                            // Apply coordinate offset for merging files with different coordinate systems
                            var adjustedX = grid.TileX + coordOffsetX;
                            var adjustedY = grid.TileY + coordOffsetY;

                            // Create grid data with adjusted coordinates
                            var gridData = new GridData
                            {
                                Id = grid.GridIdString,
                                Map = mapId,
                                Coord = new Coord(adjustedX, adjustedY),
                                NextUpdate = DateTime.UtcNow.AddMinutes(-1),
                                TenantId = tenantId
                            };

                            // Compute paths using adjusted coordinates
                            var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), "0",
                                $"{adjustedX}_{adjustedY}.png");
                            var fullPath = Path.Combine(gridStorage, relativePath);

                            // Render tile (CPU-bound)
                            var tileImage = await RenderGridTileAsync(grid, tileResourceService);

                            // Send to consumer
                            await channel.Writer.WriteAsync(
                                new RenderedGrid(grid, gridData, tileImage, relativePath, fullPath),
                                cancellationToken);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    renderTasks.Add(renderTask);

                    // Process tasks in batches to avoid unbounded list growth
                    if (renderTasks.Count >= TASK_BATCH_SIZE)
                    {
                        await Task.WhenAll(renderTasks);
                        renderTasks.Clear();
                    }
                }

                // Process remaining tasks
                if (renderTasks.Count > 0)
                {
                    await Task.WhenAll(renderTasks);
                    renderTasks.Clear();
                }
            }
            catch (Exception ex)
            {
                producerException = ex;
                throw;
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // Consumer task: Sequential I/O and batched DB writes
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var rendered in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Ensure directory exists
                        var directory = Path.GetDirectoryName(rendered.FullPath)!;
                        if (!Directory.Exists(directory))
                            Directory.CreateDirectory(directory);

                        // Save tile to disk
                        await rendered.TileImage.SaveAsPngAsync(rendered.FullPath, cancellationToken);
                        var fileSize = (int)new FileInfo(rendered.FullPath).Length;

                        // Create tile data
                        var tileData = new TileData
                        {
                            MapId = mapId,
                            Coord = rendered.GridData.Coord,
                            Zoom = 0,
                            File = rendered.RelativePath,
                            Cache = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            TenantId = tenantId,
                            FileSizeBytes = fileSize
                        };

                        // Add to batch
                        batchContext.AddGrid(rendered.GridData);
                        batchContext.AddTile(tileData);
                        batchContext.AddStorage(fileSize / (1024.0 * 1024.0));
                        importedGridIds.Add(rendered.GridData.Id);

                        // Add overlays to batch (claims, villages, provinces)
                        foreach (var overlay in rendered.SourceGrid.Overlays)
                        {
                            var overlayType = ParseOverlayType(overlay.ResourceName);
                            if (overlayType == null) continue;

                            batchContext.AddOverlay(new OverlayData
                            {
                                MapId = mapId,
                                Coord = rendered.GridData.Coord,
                                OverlayType = overlayType,
                                Data = overlay.Data,
                                TenantId = tenantId,
                                UpdatedAt = DateTime.UtcNow
                            });
                        }

                        // Report progress
                        processedCount++;
                        var overallProcessed = processedSoFar + gridsSkipped + processedCount;
                        tracker.Report(
                            "Importing grids",
                            overallProcessed,
                            totalGridsOverall,
                            $"Grid {rendered.SourceGrid.TileX},{rendered.SourceGrid.TileY}"
                        );

                        // Flush batch if needed
                        if (batchContext.ShouldFlush())
                        {
                            await FlushBatchAsync(batchContext);
                        }
                    }
                    finally
                    {
                        rendered.TileImage.Dispose();
                    }
                }

                // Flush remaining items
                if (batchContext.HasPendingItems)
                {
                    await FlushBatchAsync(batchContext);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drain remaining items to dispose images
                while (channel.Reader.TryRead(out var item))
                {
                    item.TileImage.Dispose();
                }
                throw;
            }
        }, cancellationToken);

        // Wait for both tasks
        try
        {
            await Task.WhenAll(producerTask, consumerTask);
        }
        catch (Exception) when (producerException != null)
        {
            // Log the producer exception if it was the root cause
            _logger.LogError(producerException, "Producer task failed during import");
            throw;
        }
        finally
        {
            // Drain any remaining items that weren't consumed (e.g., if consumer failed)
            while (channel.Reader.TryRead(out var orphanedItem))
            {
                orphanedItem.TileImage.Dispose();
            }
        }

        gridsImported = processedCount;
        createdGridIds.AddRange(importedGridIds);

        // Clear memory cache periodically to prevent memory buildup
        tileResourceService.ClearMemoryCache();

        return (mapId, isNewMap, gridsImported, gridsSkipped, createdGridIds, segmentGridCount);
    }

    private async Task FlushBatchAsync(BatchImportContext batch)
    {
        var (grids, tiles, overlays, storageMB) = batch.ExtractBatch();

        if (grids.Count > 0)
        {
            // Re-check for existing grids before insert to handle:
            // 1. Cross-segment duplicates (same grid ID in multiple segments)
            // 2. Race conditions from concurrent processing
            // 3. Grids inserted by previous batches
            await _gridRepository.SaveGridsBatchAsync(grids, skipExistenceCheck: false);
        }

        if (tiles.Count > 0)
        {
            // Base tiles may have duplicates in edge cases (orphaned tiles, partial failures)
            await _tileRepository.SaveTilesBatchAsync(tiles, skipExistenceCheck: false);
        }

        if (overlays.Count > 0)
        {
            await _overlayRepository.UpsertBatchAsync(overlays);
        }

        if (storageMB > 0 && grids.Count > 0)
        {
            // Use the first grid's TenantId for quota update
            await _quotaService.IncrementStorageUsageAsync(grids[0].TenantId, storageMB);
        }

        _logger.LogDebug("Flushed batch: {GridCount} grids, {TileCount} tiles, {OverlayCount} overlays, {StorageMB:F2} MB",
            grids.Count, tiles.Count, overlays.Count, storageMB);
    }

    private async Task<int> CreateNewMapAsync(string tenantId)
    {
        var mapName = await _mapNameService.GenerateUniqueIdentifierAsync(tenantId);

        var mapInfo = new MapInfo
        {
            Id = 0, // Let SQLite auto-generate
            Name = mapName,
            Hidden = false,
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            TenantId = tenantId
        };

        await _mapRepository.SaveMapAsync(mapInfo);
        return mapInfo.Id;
    }

    private async Task<Image<Rgba32>> RenderGridTileAsync(HmapGridData grid, TileResourceService tileResourceService)
    {
        var result = new Image<Rgba32>(GRID_SIZE, GRID_SIZE);

        // Load tile textures for this grid
        // GetTileImageAsync returns clones that we own and must dispose
        var tileTex = new Image<Rgba32>?[grid.Tilesets.Count];
        for (int i = 0; i < grid.Tilesets.Count; i++)
        {
            tileTex[i] = await tileResourceService.GetTileImageAsync(grid.Tilesets[i].ResourceName);
        }

        // ===== PASS 1: Base texture sampling =====
        for (int y = 0; y < GRID_SIZE; y++)
        {
            for (int x = 0; x < GRID_SIZE; x++)
            {
                var tileIndex = y * GRID_SIZE + x;
                if (grid.TileIndices == null || tileIndex >= grid.TileIndices.Length)
                {
                    result[x, y] = new Rgba32(128, 128, 128); // Gray for missing
                    continue;
                }

                var tsetIdx = grid.TileIndices[tileIndex];
                if (tsetIdx >= tileTex.Length || tileTex[tsetIdx] == null)
                {
                    result[x, y] = new Rgba32(128, 128, 128); // Gray for missing tile
                    continue;
                }

                var tex = tileTex[tsetIdx]!;
                // Sample from tile texture using floormod for proper wrapping
                var tx = ((x % tex.Width) + tex.Width) % tex.Width;
                var ty = ((y % tex.Height) + tex.Height) % tex.Height;
                result[x, y] = tex[tx, ty];
            }
        }

        // ===== PASS 2: Ridge/cliff shading =====
        // Check height differences and darken cliffs (matching Java game's Ridges.java breakz threshold)
        if (grid.ZMap != null && grid.TileIndices != null)
        {
            const float CLIFF_THRESHOLD = 11.0f;  // Match Java game's breakz() ~20 (scaled for ZMap)
            const float CLIFF_BLEND = 0.6f;       // Softer shadow (60% toward black)

            for (int y = 1; y < GRID_SIZE - 1; y++)
            {
                for (int x = 1; x < GRID_SIZE - 1; x++)
                {
                    var idx = y * GRID_SIZE + x;
                    float z = grid.ZMap[idx];
                    bool broken = false;

                    // Check 4 cardinal neighbors for height breaks
                    if (Math.Abs(z - grid.ZMap[(y - 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[(y + 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x - 1)]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x + 1)]) > CLIFF_THRESHOLD)
                        broken = true;

                    if (broken)
                    {
                        // Darken only the cliff edge pixel (1px line, not 3x3)
                        result[x, y] = BlendToBlack(result[x, y], CLIFF_BLEND);
                    }
                }
            }
        }

        // ===== PASS 3: Tile priority borders =====
        // Draw black borders where neighbor tiles have higher priority (tile ID)
        if (grid.TileIndices != null)
        {
            for (int y = 0; y < GRID_SIZE; y++)
            {
                for (int x = 0; x < GRID_SIZE; x++)
                {
                    var idx = y * GRID_SIZE + x;
                    var tileId = grid.TileIndices[idx];

                    // Check 4 neighbors for higher tile IDs
                    bool hasHigherNeighbor = false;

                    if (x > 0 && grid.TileIndices[idx - 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && x < GRID_SIZE - 1 && grid.TileIndices[idx + 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y > 0 && grid.TileIndices[idx - GRID_SIZE] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y < GRID_SIZE - 1 && grid.TileIndices[idx + GRID_SIZE] > tileId) hasHigherNeighbor = true;

                    if (hasHigherNeighbor)
                        result[x, y] = new Rgba32(0, 0, 0, 255);  // Black border
                }
            }
        }

        // Dispose tile textures (they are clones we own)
        foreach (var img in tileTex)
        {
            img?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Blend a color toward black by the specified factor (0.0 = no change, 1.0 = pure black)
    /// </summary>
    private static Rgba32 BlendToBlack(Rgba32 color, float factor)
    {
        var f1 = (int)(factor * 255);
        var f2 = 255 - f1;
        return new Rgba32(
            (byte)((color.R * f2) / 255),
            (byte)((color.G * f2) / 255),
            (byte)((color.B * f2) / 255),
            color.A
        );
    }

    /// <summary>
    /// Converts an overlay resource name from the .hmap file to a normalized overlay type.
    /// Actual .hmap format: "gfx/tiles/overlay/cplot-f" -> "ClaimFloor"
    /// </summary>
    private static string? ParseOverlayType(string resourceName)
    {
        // Extract the last segments of the resource path
        // Format: gfx/tiles/overlay/TYPE or gfx/tiles/overlay/prov/N
        var parts = resourceName.Split('/');
        if (parts.Length < 2) return null;

        var lastPart = parts[^1].ToLowerInvariant();

        // Check for province overlays (format: gfx/tiles/overlay/prov/0-4)
        if (parts.Length >= 2 && parts[^2].ToLowerInvariant() == "prov")
        {
            return lastPart switch
            {
                "0" => "Province0",
                "1" => "Province1",
                "2" => "Province2",
                "3" => "Province3",
                "4" => "Province4",
                _ => null
            };
        }

        // Other overlay types
        return lastPart switch
        {
            "cplot-f" => "ClaimFloor",      // Claim plot floor
            "cplot-o" => "ClaimOutline",    // Claim plot outline
            "vlg-f" => "VillageFloor",      // Village floor
            "vlg-o" => "VillageOutline",    // Village outline
            "vlg-sar" => "VillageSAR",      // Village Safe Area Radius
            _ => null
        };
    }

    private async Task GenerateZoomLevelsForMapAsync(
        int mapId,
        string tenantId,
        string gridStorage,
        ImportProgressTracker? tracker = null,
        int currentMapIndex = 1,
        int totalMaps = 1,
        CancellationToken cancellationToken = default)
    {
        // Get all zoom-0 tiles for this map
        var grids = await _gridRepository.GetGridsByMapAsync(mapId);
        if (grids.Count == 0)
            return;

        // Spatial chunking for large imports to limit memory usage
        const int CHUNK_SIZE = 10000;
        if (grids.Count > CHUNK_SIZE)
        {
            var chunks = SpatialPartition(grids, CHUNK_SIZE);
            _logger.LogInformation("Large map {MapId}: splitting {GridCount} grids into {ChunkCount} chunks for zoom generation",
                mapId, grids.Count, chunks.Count);

            int chunkIndex = 0;
            foreach (var chunk in chunks)
            {
                chunkIndex++;
                await GenerateZoomLevelsForChunkAsync(mapId, chunk, tenantId, gridStorage, tracker,
                    currentMapIndex, totalMaps, chunkIndex, chunks.Count, cancellationToken);
            }
        }
        else
        {
            await GenerateZoomLevelsForChunkAsync(mapId, grids, tenantId, gridStorage, tracker,
                currentMapIndex, totalMaps, 1, 1, cancellationToken);
        }
    }

    private async Task GenerateZoomLevelsForChunkAsync(
        int mapId,
        List<GridData> grids,
        string tenantId,
        string gridStorage,
        ImportProgressTracker? tracker,
        int currentMapIndex,
        int totalMaps,
        int currentChunk,
        int totalChunks,
        CancellationToken cancellationToken)
    {
        // Pre-create directories for all zoom levels
        for (int z = 1; z <= 6; z++)
        {
            var dir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), z.ToString());
            Directory.CreateDirectory(dir);
        }

        using var cache = new ZoomTileCache();

        // Build coordinate sets for each zoom level
        var coordsByZoom = BuildZoomCoordSets(grids);

        // Calculate total for progress
        int totalZoomTiles = coordsByZoom.Values.Sum(set => set.Count);
        int processedZoomTiles = 0;

        // Build progress context string
        string mapContext = totalMaps > 1 ? $"Map {currentMapIndex}/{totalMaps}" : $"Map {mapId}";
        string chunkContext = totalChunks > 1 ? $" chunk {currentChunk}/{totalChunks}" : "";

        // Report loading phase
        tracker?.Report("Generating zoom levels", 0, totalZoomTiles,
            $"{mapContext}{chunkContext}: Loading {grids.Count} base tiles...", forceReport: true);

        // Load zoom-0 tiles into cache from disk
        await LoadZoom0TilesAsync(mapId, grids, tenantId, gridStorage, cache, cancellationToken);

        _logger.LogDebug("Zoom generation: loaded {Count} zoom-0 tiles into cache for map {MapId}",
            grids.Count, mapId);

        // Process zoom levels 1-6
        for (int zoom = 1; zoom <= 6; zoom++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var zoomCoords = coordsByZoom[zoom];
            if (zoomCoords.Count == 0)
                continue;

            processedZoomTiles = await ProcessZoomLevelParallelAsync(
                mapId, zoom, zoomCoords, tenantId, gridStorage, cache,
                tracker, processedZoomTiles, totalZoomTiles,
                mapContext, chunkContext, cancellationToken);

            // Flush pending writes after each zoom level
            await cache.FlushWritesAsync();

            var (cachedTiles, memBytes) = cache.GetStats();
            _logger.LogDebug("Zoom {Zoom} complete: {CoordCount} tiles, cache has {CachedTiles} tiles (~{MemMB:F1} MB)",
                zoom, zoomCoords.Count, cachedTiles, memBytes / (1024.0 * 1024.0));
        }

        // Report saving phase
        tracker?.Report("Generating zoom levels", totalZoomTiles, totalZoomTiles,
            $"{mapContext}{chunkContext}: Saving to database...", forceReport: true);

        // Final flush of any remaining writes
        await cache.FlushWritesAsync();

        // Batch DB write for all zoom tiles
        var (tiles, totalMB) = cache.ExtractPendingMetadata();
        if (tiles.Count > 0)
        {
            // Zoom tiles may overlap with existing tiles from previous imports
            // (multiple grids share parent zoom tiles) - must check for existing
            await _tileRepository.SaveTilesBatchAsync(tiles, skipExistenceCheck: false);
            await _quotaService.IncrementStorageUsageAsync(tenantId, totalMB);

            _logger.LogInformation("Zoom generation complete for map {MapId}: {TileCount} tiles, {StorageMB:F2} MB",
                mapId, tiles.Count, totalMB);
        }
    }

    private async Task<int> ProcessZoomLevelParallelAsync(
        int mapId,
        int zoom,
        HashSet<Coord> coords,
        string tenantId,
        string gridStorage,
        ZoomTileCache cache,
        ImportProgressTracker? tracker,
        int processedCount,
        int totalCount,
        string mapContext,
        string chunkContext,
        CancellationToken cancellationToken)
    {
        const int WORKER_COUNT = 4;
        const int CHANNEL_CAPACITY = 50;  // Increased from 20 for better buffering

        var channel = Channel.CreateBounded<RenderedZoomTile>(
            new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        Exception? producerException = null;
        int currentProcessed = processedCount;
        int zoomLevelProcessed = 0;
        int zoomLevelTotal = coords.Count;

        // Producer: parallel image compositing with batched task processing
        var producerTask = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(WORKER_COUNT);
            const int TASK_BATCH_SIZE = 100;  // Reduced from 500 for more frequent sync points
            var tasks = new List<Task>(TASK_BATCH_SIZE);

            try
            {
                foreach (var coord in coords)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(cancellationToken);

                    var renderTask = Task.Run(async () =>
                    {
                        try
                        {
                            var result = RenderZoomTile(mapId, zoom, coord, tenantId, gridStorage, cache);
                            if (result != null)
                            {
                                // Properly await channel write to avoid thread pool starvation
                                await channel.Writer.WriteAsync(result, cancellationToken);
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    tasks.Add(renderTask);

                    // Process tasks in batches to avoid unbounded list growth
                    if (tasks.Count >= TASK_BATCH_SIZE)
                    {
                        await Task.WhenAll(tasks);
                        tasks.Clear();
                    }
                }

                // Process remaining tasks
                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }
            catch (Exception ex)
            {
                producerException = ex;
                throw;
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // Consumer: cache management and write queuing
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var tile in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    // Add to cache for next zoom level (refCount = 1 if not final zoom)
                    int refCount = zoom < 6 ? 1 : 0;
                    cache.AddTile(zoom, tile.Coord, tile.Image, refCount);

                    // Queue disk write
                    cache.QueueWrite(tile.FullPath, tile.PngBytes, tile.Metadata);

                    // Decrement ref counts on sub-tiles (they're no longer needed by this parent)
                    for (int x = 0; x <= 1; x++)
                    {
                        for (int y = 0; y <= 1; y++)
                        {
                            var subCoord = new Coord(tile.Coord.X * 2 + x, tile.Coord.Y * 2 + y);
                            cache.DecrementRef(zoom - 1, subCoord);
                        }
                    }

                    currentProcessed++;
                    zoomLevelProcessed++;

                    // Report progress with detailed context
                    // Format: "Map 1/3: Zoom 2/6 (125/300 tiles)"
                    var progressDetail = $"{mapContext}{chunkContext}: Zoom {zoom}/6 ({zoomLevelProcessed}/{zoomLevelTotal} tiles)";
                    tracker?.Report("Generating zoom levels", currentProcessed, totalCount, progressDetail);

                    // Incremental flush to reduce memory peak
                    if (cache.ShouldFlush)
                    {
                        await cache.FlushWritesAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drain remaining items to dispose images
                while (channel.Reader.TryRead(out var item))
                {
                    item.Image.Dispose();
                }
                throw;
            }
        }, cancellationToken);

        // Wait for both tasks
        try
        {
            await Task.WhenAll(producerTask, consumerTask);
        }
        catch (Exception) when (producerException != null)
        {
            _logger.LogError(producerException, "Producer task failed during zoom generation");
            throw;
        }
        finally
        {
            // Drain any remaining items that weren't consumed (e.g., if consumer failed)
            while (channel.Reader.TryRead(out var orphanedItem))
            {
                orphanedItem.Image.Dispose();
            }
        }

        return currentProcessed;
    }

    private RenderedZoomTile? RenderZoomTile(
        int mapId,
        int zoom,
        Coord coord,
        string tenantId,
        string gridStorage,
        ZoomTileCache cache)
    {
        using var canvas = new Image<Rgba32>(100, 100);
        canvas.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        int loaded = 0;
        var tilesToDispose = new List<Image<Rgba32>>();

        try
        {
            // Load 4 sub-tiles from cache or disk (2x2 grid)
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    var subCoord = new Coord(coord.X * 2 + x, coord.Y * 2 + y);
                    var subTile = cache.GetTile(zoom - 1, subCoord);

                    // If not in cache, try loading from disk (cross-chunk tile)
                    if (subTile == null)
                    {
                        var tilePath = Path.Combine(gridStorage, "tenants", tenantId,
                            mapId.ToString(), (zoom - 1).ToString(), $"{subCoord.Name()}.png");
                        if (File.Exists(tilePath))
                        {
                            try
                            {
                                subTile = Image.Load<Rgba32>(tilePath);
                                tilesToDispose.Add(subTile); // Track for disposal
                            }
                            catch
                            {
                                // Failed to load from disk, skip this sub-tile
                                continue;
                            }
                        }
                    }

                    if (subTile == null)
                        continue;

                    // Resize to 50x50 and place in quadrant
                    using var resized = subTile.Clone(ctx => ctx.Resize(50, 50));
                    canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(50 * x, 50 * y), 1f));
                    loaded++;
                }
            }
        }
        finally
        {
            // Dispose tiles loaded from disk (not managed by cache)
            foreach (var tile in tilesToDispose)
            {
                tile.Dispose();
            }
        }

        if (loaded == 0)
            return null;

        // Encode to PNG bytes using fast encoder
        using var ms = new MemoryStream();
        canvas.SaveAsPng(ms, TileService.FastPngEncoder);
        var pngBytes = ms.ToArray();

        var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{coord.Name()}.png");
        var fullPath = Path.Combine(gridStorage, relativePath);

        var metadata = new TileData
        {
            MapId = mapId,
            Coord = coord,
            Zoom = zoom,
            File = relativePath,
            Cache = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TenantId = tenantId,
            FileSizeBytes = pngBytes.Length
        };

        // Clone canvas for caching (original will be disposed with 'using')
        return new RenderedZoomTile(coord, canvas.Clone(), pngBytes, fullPath, metadata);
    }

    private async Task LoadZoom0TilesAsync(
        int mapId,
        List<GridData> grids,
        string tenantId,
        string gridStorage,
        ZoomTileCache cache,
        CancellationToken cancellationToken)
    {
        foreach (var grid in grids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Load zoom-0 tile from disk
            var tilePath = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), "0", $"{grid.Coord.Name()}.png");
            if (!File.Exists(tilePath))
                continue;

            try
            {
                var image = await Image.LoadAsync<Rgba32>(tilePath, cancellationToken);
                // RefCount = 1: each zoom-0 tile is used by exactly one zoom-1 parent
                cache.AddTile(0, grid.Coord, image, 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load zoom-0 tile {Path}", tilePath);
            }
        }
    }

    private static Dictionary<int, HashSet<Coord>> BuildZoomCoordSets(List<GridData> grids)
    {
        var result = new Dictionary<int, HashSet<Coord>>();
        for (int z = 1; z <= 6; z++)
            result[z] = new HashSet<Coord>();

        foreach (var grid in grids)
        {
            var coord = grid.Coord;
            for (int zoom = 1; zoom <= 6; zoom++)
            {
                coord = coord.Parent();
                result[zoom].Add(coord);
            }
        }

        return result;
    }

    private static List<List<GridData>> SpatialPartition(List<GridData> grids, int maxPerChunk)
    {
        // Sort by coordinates for spatial locality (group nearby grids together)
        var sorted = grids
            .OrderBy(g => g.Coord.X / 100)
            .ThenBy(g => g.Coord.Y / 100)
            .ToList();

        var chunks = new List<List<GridData>>();
        for (int i = 0; i < sorted.Count; i += maxPerChunk)
        {
            chunks.Add(sorted.Skip(i).Take(maxPerChunk).ToList());
        }

        return chunks;
    }
}
