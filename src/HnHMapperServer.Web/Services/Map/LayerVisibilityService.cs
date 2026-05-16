using HnHMapperServer.Web.Models;

namespace HnHMapperServer.Web.Services.Map;

/// <summary>
/// Service for managing layer visibility state
/// </summary>
public class LayerVisibilityService
{
    private readonly ILogger<LayerVisibilityService> _logger;

    /// <summary>
    /// Whether to show player characters
    /// </summary>
    private bool _showPlayers = true;

    /// <summary>
    /// Whether to show player tooltips
    /// </summary>
    private bool _showPlayerTooltips = true;

    /// <summary>
    /// Whether to show markers
    /// </summary>
    // Markers are ON by default. Users can disable them from the sidebar.
    //
    // This fixes a confusing UX trap:
    // - If marker filter mode is OFF on initial load, users expect to see markers.
    // - Previously, default ShowMarkers=false meant "filter mode OFF" looked like "markers are broken".
    private bool _showMarkers = true;

    /// <summary>
    /// Whether to show thingwall markers
    /// </summary>
    private bool _showThingwalls = true;

    /// <summary>
    /// Whether to show thingwall tooltips
    /// </summary>
    private bool _showThingwallTooltips = false;

    /// <summary>
    /// Whether to show quest markers
    /// </summary>
    private bool _showQuests = true;

    /// <summary>
    /// Whether to show quest tooltips
    /// </summary>
    private bool _showQuestTooltips = false;

    /// <summary>
    /// Whether to show custom markers
    /// </summary>
    private bool _showCustomMarkers = true;

    /// <summary>
    /// Whether to show grid coordinates
    /// </summary>
    private bool _showGridCoordinates = false;

    /// <summary>
    /// Whether to cluster markers for performance
    /// </summary>
    private bool _showClustering = true;

    public LayerVisibilityService(ILogger<LayerVisibilityService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fired when any visibility flag changes. Subscribers should re-render.
    /// Fires after every property setter and Toggle*/ShowAll/HideAll/Reset call.
    /// </summary>
    public event Action? OnChange;

    private void Notify() => OnChange?.Invoke();

    #region Public Properties

    /// <summary>
    /// Whether to show player characters
    /// </summary>
    public bool ShowPlayers
    {
        get => _showPlayers;
        set { if (_showPlayers != value) { _showPlayers = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show player tooltips
    /// </summary>
    public bool ShowPlayerTooltips
    {
        get => _showPlayerTooltips;
        set { if (_showPlayerTooltips != value) { _showPlayerTooltips = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show markers
    /// </summary>
    public bool ShowMarkers
    {
        get => _showMarkers;
        set { if (_showMarkers != value) { _showMarkers = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show thingwall markers
    /// </summary>
    public bool ShowThingwalls
    {
        get => _showThingwalls;
        set { if (_showThingwalls != value) { _showThingwalls = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show thingwall tooltips
    /// </summary>
    public bool ShowThingwallTooltips
    {
        get => _showThingwallTooltips;
        set { if (_showThingwallTooltips != value) { _showThingwallTooltips = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show quest markers
    /// </summary>
    public bool ShowQuests
    {
        get => _showQuests;
        set { if (_showQuests != value) { _showQuests = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show quest tooltips
    /// </summary>
    public bool ShowQuestTooltips
    {
        get => _showQuestTooltips;
        set { if (_showQuestTooltips != value) { _showQuestTooltips = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show custom markers
    /// </summary>
    public bool ShowCustomMarkers
    {
        get => _showCustomMarkers;
        set { if (_showCustomMarkers != value) { _showCustomMarkers = value; Notify(); } }
    }

    /// <summary>
    /// Whether to show grid coordinates
    /// </summary>
    public bool ShowGridCoordinates
    {
        get => _showGridCoordinates;
        set { if (_showGridCoordinates != value) { _showGridCoordinates = value; Notify(); } }
    }

    /// <summary>
    /// Whether to cluster markers for performance
    /// </summary>
    public bool ShowClustering
    {
        get => _showClustering;
        set { if (_showClustering != value) { _showClustering = value; Notify(); } }
    }

    #endregion

    #region Layer Visibility Logic

    /// <summary>
    /// Determine if character tooltips should be visible
    /// </summary>
    public bool ShouldShowCharacterTooltips()
    {
        return _showPlayers && _showPlayerTooltips;
    }

    /// <summary>
    /// Determine if a marker should be visible based on its type
    /// </summary>
    public bool ShouldShowMarker(MarkerModel marker)
    {
        if (marker.Hidden)
        {
            return false;
        }

        return marker.Type switch
        {
            "thingwall" => _showThingwalls && _showMarkers,
            "quest" => _showQuests && _showMarkers,
            _ => _showMarkers
        };
    }

    /// <summary>
    /// Determine if marker tooltips should be visible for a specific type
    /// </summary>
    public bool ShouldShowMarkerTooltips(string markerType)
    {
        return markerType switch
        {
            "thingwall" => _showThingwalls && _showMarkers && _showThingwallTooltips,
            "quest" => _showQuests && _showMarkers && _showQuestTooltips,
            _ => false
        };
    }

    /// <summary>
    /// Get visibility configuration for syncing with map view
    /// </summary>
    public LayerVisibilityConfig GetVisibilityConfig()
    {
        return new LayerVisibilityConfig
        {
            ShowPlayers = _showPlayers,
            ShowPlayerTooltips = _showPlayerTooltips,
            ShowMarkers = _showMarkers,
            ShowThingwalls = _showThingwalls,
            ShowThingwallTooltips = _showThingwallTooltips,
            ShowQuests = _showQuests,
            ShowQuestTooltips = _showQuestTooltips,
            ShowCustomMarkers = _showCustomMarkers,
            ShowGridCoordinates = _showGridCoordinates,
            ShowClustering = _showClustering
        };
    }

    /// <summary>
    /// Set visibility from configuration
    /// </summary>
    public void SetVisibilityConfig(LayerVisibilityConfig config)
    {
        _showPlayers = config.ShowPlayers;
        _showPlayerTooltips = config.ShowPlayerTooltips;
        _showMarkers = config.ShowMarkers;
        _showThingwalls = config.ShowThingwalls;
        _showThingwallTooltips = config.ShowThingwallTooltips;
        _showQuests = config.ShowQuests;
        _showQuestTooltips = config.ShowQuestTooltips;
        _showCustomMarkers = config.ShowCustomMarkers;
        _showGridCoordinates = config.ShowGridCoordinates;
        _showClustering = config.ShowClustering;
        Notify();
    }

    #endregion

    #region Toggle Methods

    /// <summary>Toggle player visibility</summary>
    public void TogglePlayers() { ShowPlayers = !_showPlayers; _logger.LogDebug("Players visibility toggled: {Visible}", _showPlayers); }

    /// <summary>Toggle player tooltips</summary>
    public void TogglePlayerTooltips() { ShowPlayerTooltips = !_showPlayerTooltips; _logger.LogDebug("Player tooltips toggled: {Visible}", _showPlayerTooltips); }

    /// <summary>Toggle marker visibility</summary>
    public void ToggleMarkers() { ShowMarkers = !_showMarkers; _logger.LogDebug("Markers visibility toggled: {Visible}", _showMarkers); }

    /// <summary>Toggle thingwall visibility</summary>
    public void ToggleThingwalls() { ShowThingwalls = !_showThingwalls; _logger.LogDebug("Thingwalls visibility toggled: {Visible}", _showThingwalls); }

    /// <summary>Toggle thingwall tooltips</summary>
    public void ToggleThingwallTooltips() { ShowThingwallTooltips = !_showThingwallTooltips; _logger.LogDebug("Thingwall tooltips toggled: {Visible}", _showThingwallTooltips); }

    /// <summary>Toggle quest visibility</summary>
    public void ToggleQuests() { ShowQuests = !_showQuests; _logger.LogDebug("Quests visibility toggled: {Visible}", _showQuests); }

    /// <summary>Toggle quest tooltips</summary>
    public void ToggleQuestTooltips() { ShowQuestTooltips = !_showQuestTooltips; _logger.LogDebug("Quest tooltips toggled: {Visible}", _showQuestTooltips); }

    /// <summary>Toggle custom marker visibility</summary>
    public void ToggleCustomMarkers() { ShowCustomMarkers = !_showCustomMarkers; _logger.LogDebug("Custom markers visibility toggled: {Visible}", _showCustomMarkers); }

    /// <summary>Toggle grid coordinates</summary>
    public void ToggleGridCoordinates() { ShowGridCoordinates = !_showGridCoordinates; _logger.LogDebug("Grid coordinates toggled: {Visible}", _showGridCoordinates); }

    #endregion

    #region Batch Operations

    /// <summary>
    /// Show all layers
    /// </summary>
    public void ShowAll()
    {
        _showPlayers = true;
        _showPlayerTooltips = true;
        _showMarkers = true;
        _showThingwalls = true;
        _showThingwallTooltips = true;
        _showQuests = true;
        _showQuestTooltips = true;
        _showCustomMarkers = true;
        _logger.LogInformation("All layers shown");
        Notify();
    }

    /// <summary>
    /// Hide all layers (except grid coordinates)
    /// </summary>
    public void HideAll()
    {
        _showPlayers = false;
        _showPlayerTooltips = false;
        _showMarkers = false;
        _showThingwalls = false;
        _showThingwallTooltips = false;
        _showQuests = false;
        _showQuestTooltips = false;
        _showCustomMarkers = false;
        _logger.LogInformation("All layers hidden");
        Notify();
    }

    /// <summary>
    /// Reset to default visibility
    /// </summary>
    public void ResetToDefaults()
    {
        _showPlayers = true;
        _showPlayerTooltips = true;
        _showMarkers = true;
        _showThingwalls = true;
        _showThingwallTooltips = false;
        _showQuests = true;
        _showQuestTooltips = false;
        _showCustomMarkers = true;
        _showGridCoordinates = false;
        _logger.LogInformation("Layer visibility reset to defaults");
        Notify();
    }

    #endregion
}

/// <summary>
/// Layer visibility configuration snapshot
/// </summary>
public class LayerVisibilityConfig
{
    public bool ShowPlayers { get; set; }
    public bool ShowPlayerTooltips { get; set; }
    public bool ShowMarkers { get; set; }
    public bool ShowThingwalls { get; set; }
    public bool ShowThingwallTooltips { get; set; }
    public bool ShowQuests { get; set; }
    public bool ShowQuestTooltips { get; set; }
    public bool ShowCustomMarkers { get; set; }
    public bool ShowGridCoordinates { get; set; }
    public bool ShowClustering { get; set; } = true;
}
