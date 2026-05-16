using HnHMapperServer.Web.Models;

namespace HnHMapperServer.Web.Services.Map;

/// <summary>
/// Service for managing custom marker state and rendering coordination
/// </summary>
public class CustomMarkerStateService
{
    private readonly ILogger<CustomMarkerStateService> _logger;

    /// <summary>
    /// List of all custom markers (across all maps)
    /// </summary>
    private List<CustomMarkerViewModel> _allCustomMarkers = new();

    /// <summary>
    /// Whether custom markers have been rendered to the map
    /// </summary>
    private bool _customMarkersRendered = false;

    public CustomMarkerStateService(ILogger<CustomMarkerStateService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fired whenever the underlying marker collection changes (set/add/update/remove/clear).
    /// Subscribers should re-render.
    /// </summary>
    public event Action? OnChange;

    private void Notify() => OnChange?.Invoke();

    #region Public Properties

    /// <summary>
    /// All custom markers (read-only)
    /// </summary>
    public IReadOnlyList<CustomMarkerViewModel> AllCustomMarkers => _allCustomMarkers.AsReadOnly();

    /// <summary>
    /// Whether custom markers have been rendered
    /// </summary>
    public bool CustomMarkersRendered => _customMarkersRendered;

    #endregion

    #region State Management

    /// <summary>
    /// Set all custom markers (typically from API fetch)
    /// </summary>
    public void SetCustomMarkers(List<CustomMarkerViewModel> markers)
    {
        _allCustomMarkers = markers;
        _customMarkersRendered = false; // Need to re-render
        _logger.LogDebug("Custom markers loaded: {Count} markers", markers.Count);
        Notify();
    }

    /// <summary>
    /// Get custom markers for a specific map (excludes hidden)
    /// </summary>
    public IEnumerable<CustomMarkerViewModel> GetCustomMarkersForMap(int mapId)
    {
        return _allCustomMarkers.Where(m => m.MapId == mapId && !m.Hidden);
    }

    /// <summary>
    /// Get custom marker by ID
    /// </summary>
    public CustomMarkerViewModel? GetCustomMarkerById(int markerId)
    {
        return _allCustomMarkers.FirstOrDefault(m => m.Id == markerId);
    }

    /// <summary>
    /// Add or update a custom marker (from SSE or dialog)
    /// </summary>
    public void AddOrUpdateCustomMarker(CustomMarkerViewModel marker)
    {
        // Normalize timestamps (API returns UTC but kind may be unspecified)
        if (marker.PlacedAt.Kind == DateTimeKind.Unspecified)
        {
            marker.PlacedAt = DateTime.SpecifyKind(marker.PlacedAt, DateTimeKind.Utc);
        }
        if (marker.UpdatedAt.Kind == DateTimeKind.Unspecified)
        {
            marker.UpdatedAt = DateTime.SpecifyKind(marker.UpdatedAt, DateTimeKind.Utc);
        }

        // Update local cache
        _allCustomMarkers.RemoveAll(m => m.Id == marker.Id);
        _allCustomMarkers.Insert(0, marker);

        // Keep sidebar ordering (newest first)
        _allCustomMarkers = _allCustomMarkers
            .OrderByDescending(m => m.PlacedAt)
            .ToList();

        _logger.LogDebug("Custom marker added/updated: Id={Id}, Title={Title}", marker.Id, marker.Title);
        Notify();
    }

    /// <summary>
    /// Remove custom marker by ID
    /// </summary>
    public bool RemoveCustomMarker(int markerId)
    {
        var removed = _allCustomMarkers.RemoveAll(m => m.Id == markerId);
        if (removed > 0)
        {
            _logger.LogDebug("Custom marker removed: Id={Id}", markerId);
            Notify();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear all custom markers
    /// </summary>
    public void Clear()
    {
        _allCustomMarkers.Clear();
        _customMarkersRendered = false;
        _logger.LogDebug("All custom markers cleared");
        Notify();
    }

    #endregion

    #region Rendering State

    /// <summary>
    /// Mark custom markers as rendered
    /// </summary>
    public void MarkAsRendered()
    {
        _customMarkersRendered = true;
        _logger.LogDebug("Custom markers marked as rendered");
    }

    /// <summary>
    /// Mark custom markers as needing re-render (e.g., after map change)
    /// </summary>
    public void MarkAsNeedingRender()
    {
        _customMarkersRendered = false;
        _logger.LogDebug("Custom markers marked as needing render");
    }

    /// <summary>
    /// Check if custom markers need rendering
    /// </summary>
    public bool NeedsRendering(int currentMapId, bool showCustomMarkers)
    {
        if (_customMarkersRendered)
        {
            _logger.LogDebug("Custom markers already rendered; skipping");
            return false;
        }

        if (!showCustomMarkers)
        {
            _logger.LogDebug("Custom markers layer hidden; render deferred");
            return false;
        }

        if (_allCustomMarkers.Count == 0)
        {
            _logger.LogDebug("No custom markers loaded for map {MapId}; marking as rendered", currentMapId);
            _customMarkersRendered = true;
            return false;
        }

        return true;
    }

    #endregion

    #region Queries

    /// <summary>
    /// Count custom markers by map
    /// </summary>
    public int CountCustomMarkersForMap(int mapId)
    {
        return _allCustomMarkers.Count(m => m.MapId == mapId && !m.Hidden);
    }

    /// <summary>
    /// Get custom markers by creator
    /// </summary>
    public IEnumerable<CustomMarkerViewModel> GetCustomMarkersByCreator(string username)
    {
        return _allCustomMarkers.Where(m => m.CreatedBy.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Search custom markers by title
    /// </summary>
    public IEnumerable<CustomMarkerViewModel> SearchCustomMarkers(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _allCustomMarkers.Where(m => !m.Hidden);
        }

        return _allCustomMarkers.Where(m => !m.Hidden &&
            (m.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
             (m.Description != null && m.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase))));
    }

    #endregion
}
