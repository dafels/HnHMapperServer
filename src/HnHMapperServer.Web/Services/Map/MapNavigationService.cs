using HnHMapperServer.Web.Models;

namespace HnHMapperServer.Web.Services.Map;

/// <summary>
/// Service for managing map navigation state and URL synchronization
/// </summary>
public class MapNavigationService
{
    private readonly ILogger<MapNavigationService> _logger;

    /// <summary>
    /// List of all available maps
    /// </summary>
    private List<MapInfoModel> _maps = new();

    /// <summary>
    /// Currently selected map
    /// </summary>
    private MapInfoModel? _selectedMap;

    /// <summary>
    /// Currently selected overlay map
    /// </summary>
    private MapInfoModel? _overlayMap;

    /// <summary>
    /// Current map ID
    /// </summary>
    private int _currentMapId = 0;

    /// <summary>
    /// Current center X coordinate (grid coordinates)
    /// </summary>
    private double _centerX = 0;

    /// <summary>
    /// Current center Y coordinate (grid coordinates)
    /// </summary>
    private double _centerY = 0;

    /// <summary>
    /// Current zoom level
    /// </summary>
    private int _zoom = 7;

    /// <summary>
    /// Overlay map ID (null if no overlay)
    /// </summary>
    private int? _overlayMapId = null;

    /// <summary>
    /// Overlay X offset for map comparison
    /// </summary>
    private double _overlayOffsetX = 0;

    /// <summary>
    /// Overlay Y offset for map comparison
    /// </summary>
    private double _overlayOffsetY = 0;

    /// <summary>
    /// Per-map revision numbers received via SSE (used for cache busting and UI version indicator)
    /// </summary>
    private readonly Dictionary<int, int> _mapRevisions = new();

    public MapNavigationService(ILogger<MapNavigationService> logger)
    {
        _logger = logger;
    }

    #region Public Properties

    /// <summary>
    /// All available maps (read-only)
    /// </summary>
    public IReadOnlyList<MapInfoModel> Maps => _maps.AsReadOnly();

    /// <summary>
    /// Currently selected map
    /// </summary>
    public MapInfoModel? SelectedMap
    {
        get => _selectedMap;
        set => _selectedMap = value;
    }

    /// <summary>
    /// Currently selected overlay map
    /// </summary>
    public MapInfoModel? OverlayMap
    {
        get => _overlayMap;
        set => _overlayMap = value;
    }

    /// <summary>
    /// Current map ID
    /// </summary>
    public int CurrentMapId
    {
        get => _currentMapId;
        set => _currentMapId = value;
    }

    /// <summary>
    /// Current center X coordinate
    /// </summary>
    public double CenterX
    {
        get => _centerX;
        set => _centerX = value;
    }

    /// <summary>
    /// Current center Y coordinate
    /// </summary>
    public double CenterY
    {
        get => _centerY;
        set => _centerY = value;
    }

    /// <summary>
    /// Current zoom level
    /// </summary>
    public int Zoom
    {
        get => _zoom;
        set => _zoom = value;
    }

    /// <summary>
    /// Overlay map ID
    /// </summary>
    public int? OverlayMapId
    {
        get => _overlayMapId;
        set => _overlayMapId = value;
    }

    /// <summary>
    /// Overlay X offset for map comparison
    /// </summary>
    public double OverlayOffsetX
    {
        get => _overlayOffsetX;
        set => _overlayOffsetX = value;
    }

    /// <summary>
    /// Overlay Y offset for map comparison
    /// </summary>
    public double OverlayOffsetY
    {
        get => _overlayOffsetY;
        set => _overlayOffsetY = value;
    }

    /// <summary>
    /// Get current revision for a map
    /// </summary>
    public int GetMapRevision(int mapId)
    {
        return _mapRevisions.TryGetValue(mapId, out var revision) ? revision : 1;
    }

    /// <summary>
    /// Get current map's revision
    /// </summary>
    public int CurrentRevision => GetMapRevision(_currentMapId);

    #endregion

    #region Map Management

    /// <summary>
    /// Canonical map ordering used everywhere (init load, live upserts, panel refetch):
    /// main map first, then priority (descending), then name.
    /// </summary>
    private static List<MapInfoModel> SortMaps(IEnumerable<MapInfoModel> maps) =>
        maps.OrderByDescending(m => m.MapInfo.IsMainMap)
            .ThenByDescending(m => m.MapInfo.Priority)
            .ThenBy(m => m.MapInfo.Name)
            .ToList();

    /// <summary>
    /// Set the list of available maps
    /// </summary>
    public void SetMaps(List<MapInfoModel> maps)
    {
        _maps = SortMaps(maps);
        _logger.LogDebug("Maps loaded: {Count} maps", maps.Count);
    }

    /// <summary>
    /// Add or update a map in the list
    /// </summary>
    public void AddOrUpdateMap(MapInfoModel map)
    {
        var existingIndex = _maps.FindIndex(m => m.ID == map.ID);
        if (existingIndex >= 0)
        {
            _maps[existingIndex] = map;
        }
        else
        {
            _maps.Add(map);
        }

        _maps = SortMaps(_maps);

        _logger.LogDebug("Map added/updated: Id={Id}, Name={Name}", map.ID, map.MapInfo.Name);
    }

    /// <summary>
    /// Remove a map from the list
    /// </summary>
    public bool RemoveMap(int mapId)
    {
        var map = _maps.FirstOrDefault(m => m.ID == mapId);
        if (map != null)
        {
            _maps.Remove(map);
            _logger.LogDebug("Map removed: Id={Id}, Name={Name}", mapId, map.MapInfo.Name);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Get map by ID
    /// </summary>
    public MapInfoModel? GetMapById(int mapId)
    {
        return _maps.FirstOrDefault(m => m.ID == mapId);
    }

    /// <summary>
    /// Get map name by ID (for display in player/marker lists)
    /// </summary>
    public string GetMapName(int mapId)
    {
        var map = GetMapById(mapId);
        return map?.MapInfo.Name ?? $"Map {mapId}";
    }

    /// <summary>
    /// Get first visible map
    /// </summary>
    public MapInfoModel? GetFirstVisibleMap()
    {
        return _maps.FirstOrDefault(m => !m.MapInfo.Hidden);
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Update current position from map drag/zoom
    /// </summary>
    public void UpdatePosition(int x, int y, int z)
    {
        _centerX = x;
        _centerY = y;
        _zoom = z;
        _logger.LogDebug("Position updated: ({X}, {Y}) @ zoom {Z}", x, y, z);
    }

    /// <summary>
    /// Change current map
    /// </summary>
    public void ChangeMap(int mapId)
    {
        _currentMapId = mapId;
        _selectedMap = GetMapById(mapId);
        _logger.LogInformation("Map changed to {MapId} ({Name})", mapId, _selectedMap?.MapInfo.Name ?? "Unknown");
    }

    /// <summary>
    /// Change overlay map. Does not reset offset - caller should load saved offset from API.
    /// </summary>
    public void ChangeOverlayMap(int? mapId)
    {
        _overlayMapId = mapId;
        _overlayMap = mapId.HasValue ? GetMapById(mapId.Value) : null;
        // Don't reset offset - Map.razor.cs loads saved offset from API
        // If clearing overlay (mapId=null), reset offset to 0
        if (!mapId.HasValue)
        {
            _overlayOffsetX = 0;
            _overlayOffsetY = 0;
        }
        _logger.LogInformation("Overlay map changed to {MapId}", mapId?.ToString() ?? "None");
    }

    /// <summary>
    /// Set overlay offset for map comparison
    /// </summary>
    public void SetOverlayOffset(double offsetX, double offsetY)
    {
        _overlayOffsetX = offsetX;
        _overlayOffsetY = offsetY;
        _logger.LogDebug("Overlay offset set to ({OffsetX}, {OffsetY})", offsetX, offsetY);
    }

    /// <summary>
    /// Get URL for current state (uses query strings to avoid triggering Blazor lifecycle)
    /// </summary>
    public string GetCurrentUrl()
    {
        return $"/map?map={_currentMapId}&x={_centerX:F0}&y={_centerY:F0}&z={_zoom}";
    }

    /// <summary>
    /// Get URL for specific coordinates (uses query strings to avoid triggering Blazor lifecycle)
    /// </summary>
    public string GetUrl(int mapId, int x, int y, int z)
    {
        return $"/map?map={mapId}&x={x}&y={y}&z={z}";
    }

    /// <summary>
    /// Get URL for character tracking (uses query strings to avoid triggering Blazor lifecycle)
    /// </summary>
    public string GetCharacterUrl(int characterId)
    {
        return $"/map?character={characterId}";
    }

    #endregion

    #region Revision Management

    /// <summary>
    /// Set map revision (for cache busting)
    /// </summary>
    public void SetMapRevision(int mapId, int revision)
    {
        var oldRevision = _mapRevisions.TryGetValue(mapId, out var old) ? old : 0;
        _mapRevisions[mapId] = revision;

        if (oldRevision != revision)
        {
            _logger.LogDebug("Map {MapId} revision updated: {OldRevision} → {NewRevision}", mapId, oldRevision, revision);
        }
    }

    /// <summary>
    /// Initialize revision for a map
    /// </summary>
    public void InitializeMapRevision(int mapId, int revision)
    {
        if (!_mapRevisions.ContainsKey(mapId))
        {
            _mapRevisions[mapId] = revision;
            _logger.LogDebug("Map {MapId} initial revision set to {Revision}", mapId, revision);
        }
    }

    /// <summary>
    /// Clear all revisions
    /// </summary>
    public void ClearRevisions()
    {
        _mapRevisions.Clear();
    }

    #endregion

    #region Queries

    /// <summary>
    /// Check if a map exists
    /// </summary>
    public bool MapExists(int mapId)
    {
        return _maps.Any(m => m.ID == mapId);
    }

    /// <summary>
    /// Get visible maps
    /// </summary>
    public IEnumerable<MapInfoModel> GetVisibleMaps()
    {
        return _maps.Where(m => !m.MapInfo.Hidden);
    }

    /// <summary>
    /// Count total maps
    /// </summary>
    public int GetMapCount() => _maps.Count;

    /// <summary>
    /// Count visible maps
    /// </summary>
    public int GetVisibleMapCount() => _maps.Count(m => !m.MapInfo.Hidden);

    #endregion
}
