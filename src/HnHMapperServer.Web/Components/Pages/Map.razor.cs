using HnHMapperServer.Web.Models;
using HnHMapperServer.Web.Services;
using HnHMapperServer.Web.Services.Map;
using HnHMapperServer.Web.Components.Map;
using HnHMapperServer.Web.Components.Map.Sidebar;
using HnHMapperServer.Web.Components.Map.Dialogs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using System.Net.Http.Json;
using System.Text.Json;
using System.Linq;

namespace HnHMapperServer.Web.Components.Pages;

/// <summary>
/// Main map viewer page component - coordinates map UI components and services.
/// Refactored to use specialized services for better maintainability.
/// </summary>
[Authorize]
public partial class Map : IAsyncDisposable, IBrowserViewportObserver
{
    /// <summary>
    /// Shared JSON options matching API defaults (camelCase, case-insensitive)
    /// </summary>
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new(JsonSerializerDefaults.Web);

    #region Query String Parameters (parsed from URL for bookmarks)

    private int? MapId { get; set; }
    private int? GridX { get; set; }
    private int? GridY { get; set; }
    private int? Zoom { get; set; }
    private int? CharacterId { get; set; }

    #endregion

    #region Injected Services

    [Inject] private MapDataService MapData { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<Map> Logger { get; set; } = default!;
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private AuthenticationStateCache AuthStateCache { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ReconnectionState ReconnectionState { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private IBrowserViewportService BrowserViewportService { get; set; } = default!;

    // New specialized services
    [Inject] private CharacterTrackingService CharacterTracking { get; set; } = default!;
    [Inject] private MarkerStateService MarkerState { get; set; } = default!;
    [Inject] private CustomMarkerStateService CustomMarkerState { get; set; } = default!;
    [Inject] private MapNavigationService MapNavigation { get; set; } = default!;
    [Inject] private LayerVisibilityService LayerVisibility { get; set; } = default!;
    [Inject] private SafeJsInterop SafeJs { get; set; } = default!;
    [Inject] private IBuildInfoProvider BuildInfo { get; set; } = default!;

    #endregion

    /// <summary>
    /// Version suffix for cache busting dynamic JS imports (uses commit hash from BuildInfo)
    /// </summary>
    private string JsVersion => $"?v={BuildInfo.Get("web").Commit}";

    #region Component References

    private MapView? mapView;

    #endregion

    #region Component State (Reduced!)

    private MapState state = new();
    private bool isLoaded = false;
    private bool hasMapPermission = false;
    private bool hasMarkersPermission = false;
    private bool isTenantAdmin => state.TenantRole == "TenantAdmin" || state.TenantRole == "SuperAdmin";
    private bool isReconnecting = false;
    private bool circuitFullyReady = false;  // Prevents JS->NET calls during circuit initialization

    private Timer? markerUpdateTimer;
    private Timer? permissionCheckTimer;
    private int markerUpdateRetryCount = 0;
    private const int MaxMarkerUpdateRetries = 3;

    private List<TimerDto> allTimers = new();

    private IJSObjectReference? sseModule;
    private DotNetObjectReference<Map>? sseDotnetRef;
    private bool sseInitialized;
    private IJSObjectReference? leafletModule; // Cached leaflet-interop module for ping operations

    // Locks to prevent race conditions during initialization
    private readonly SemaphoreSlim sseCallbackLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim customMarkerLock = new SemaphoreSlim(1, 1);

    // Hidden marker groups (by image path) - persisted to localStorage
    private HashSet<string> hiddenMarkerGroups = new();
    private const string HiddenMarkerGroupsStorageKey = "hiddenMarkerGroups";

    // Floating button toggle states - persisted to localStorage
    private const string ToggleStatesStorageKey = "mapToggleStates";

    #endregion

    #region Sidebar State

    private bool isSidebarOpen = false;
    private SidebarMode sidebarMode = SidebarMode.Players;
    private bool hasActivePing = false;
    private bool controlsHintExpanded = false; // Controls hint panel collapsed by default

    #endregion

    #region Context Menu State

    private bool showContextMenu = false;
    private bool showMarkerContextMenu = false;
    private bool showCustomMarkerContextMenu = false;
    private bool showMapActionMenu = false; // New: for Create Marker/Ping menu

    // Overlay toggle state (floating buttons)
    private bool showPClaim = false; // Player claims (red) - off by default
    private bool showVClaim = false; // Village claims (orange) - off by default
    private bool showProvince = false; // Province overlay - off by default
    private bool showThingwallHighlight = false; // Thingwall highlighting (cyan) - off by default
    private bool showQuestGiverHighlight = false; // Quest giver highlighting (green) - off by default
    private bool showMarkerFilterMode = false; // Marker filter mode - off by default
    private bool showClustering = true; // Marker clustering - on by default for performance
    private bool showRoads = true; // Roads visibility - on by default
    private int contextMenuX = 0;
    private int contextMenuY = 0;
    private (int x, int y) contextCoords;
    private int contextMarkerId = 0;
    private int contextCustomMarkerId = 0;
    private bool markerHasTimer = false; // Whether the context menu marker has an active timer
    private bool customMarkerHasTimer = false; // Whether the context menu custom marker has an active timer

    // Road state
    private List<RoadViewModel> allRoads = new();
    private bool showRoadContextMenu = false;
    private int contextRoadId = 0;
    private bool contextRoadCanEdit = false;
    private bool isDrawingRoad = false;
    private int drawingPointsCount = 0;

    // Navigation state
    private RouteResult? currentRoute = null;
    private (int coordX, int coordY, int x, int y)? navigationStartPoint = null;

    // Map action menu context (for marker/ping creation)
    private int mapActionMapId = 0;
    private int mapActionCoordX = 0;
    private int mapActionCoordY = 0;
    private int mapActionX = 0;
    private int mapActionY = 0;

    // Tile info dialog state
    private bool showTileInfoDialog = false;
    private TileInfoResult? tileInfoResult = null;
    private bool tileInfoLoading = false;

    #endregion

    #region Computed Properties (Delegated to Services)

    // Player/Character state (for razor view)
    private IEnumerable<CharacterModel> FilteredPlayers => CharacterTracking.FilteredPlayers;
    private IReadOnlyList<CharacterModel> allCharacters => CharacterTracking.AllCharacters;
    private bool isFollowing => CharacterTracking.IsFollowing;
    private int? followingCharacterId => CharacterTracking.FollowingCharacterId;

    // My character identification (persisted in localStorage)
    private string? myCharacterName;

    private string PlayerFilter
    {
        get => CharacterTracking.PlayerFilter;
        set => CharacterTracking.PlayerFilter = value;
    }

    // Marker state (for razor view)
    private IEnumerable<MarkerModel> FilteredMarkers => MarkerState.FilteredMarkers;
    private IReadOnlyList<MarkerModel> allMarkers => MarkerState.AllMarkers;

    private string MarkerFilter
    {
        get => MarkerState.MarkerFilter;
        set => MarkerState.MarkerFilter = value;
    }

    // Custom marker state (for razor view)
    private IReadOnlyList<CustomMarkerViewModel> allCustomMarkers => CustomMarkerState.AllCustomMarkers;

    // Map navigation state (for razor view)
    private IReadOnlyList<MapInfoModel> maps => MapNavigation.Maps;

    private MapInfoModel? selectedMap
    {
        get => MapNavigation.SelectedMap;
        set => MapNavigation.SelectedMap = value;
    }

    private MapInfoModel? overlayMap
    {
        get => MapNavigation.OverlayMap;
        set => MapNavigation.OverlayMap = value;
    }

    // Legacy computed properties (PascalCase for backward compatibility)
    private bool IsFollowing => CharacterTracking.IsFollowing;
    private int? FollowingCharacterId => CharacterTracking.FollowingCharacterId;

    private MapInfoModel? SelectedMap
    {
        get => MapNavigation.SelectedMap;
        set => MapNavigation.SelectedMap = value;
    }

    private MapInfoModel? OverlayMap
    {
        get => MapNavigation.OverlayMap;
        set => MapNavigation.OverlayMap = value;
    }

    #endregion

    #region Lifecycle Methods

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // Parse query string parameters for bookmark support
            var uri = new Uri(Navigation.Uri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            if (int.TryParse(query["map"], out var mapId)) MapId = mapId;
            if (int.TryParse(query["x"], out var x)) GridX = x;
            if (int.TryParse(query["y"], out var y)) GridY = y;
            if (int.TryParse(query["z"], out var z)) Zoom = z;
            if (int.TryParse(query["character"], out var characterId)) CharacterId = characterId;

            Logger.LogInformation("URL Parameters parsed - MapId: {MapId}, GridX: {GridX}, GridY: {GridY}, Zoom: {Zoom}, CharacterId: {CharacterId}",
                MapId, GridX, GridY, Zoom, CharacterId);

            // Check for Map permission first
            var config = await MapData.GetConfigAsync();

            hasMapPermission = config.Permissions.Any(a => a.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));

            if (!hasMapPermission)
            {
                permissionCheckTimer = new Timer(CheckForMapPermission, null, 10000, 10000);
                Logger.LogInformation("User lacks Map permission. Starting permission check timer.");
                return;
            }

            // Load initial data in parallel
            var markersTask = MapData.GetMarkersAsync();
            var mapsTask = MapData.GetMapsAsync();

            await Task.WhenAll(markersTask, mapsTask);

            var markers = await markersTask;
            var mapsDict = await mapsTask;

            // Populate services
            MarkerState.SetMarkers(markers);

            // Sort maps: main map first, then by priority and name
            var mapsList = mapsDict.Values
                .OrderByDescending(m => m.MapInfo.IsMainMap)  // Main map first
                .ThenByDescending(m => m.MapInfo.Priority)     // Higher priority next
                .ThenBy(m => m.MapInfo.Name)                   // Alphabetically
                .ToList();
            MapNavigation.SetMaps(mapsList);

            state.Permissions = config.Permissions;
            state.TenantRole = config.TenantRole ?? "";

            // Check permissions
            hasMapPermission = state.Permissions.Any(a => a.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
            hasMarkersPermission = state.Permissions.Any(a => a.Equals(Permission.Markers.ToClaimValue(), StringComparison.OrdinalIgnoreCase));

            // Determine initial map selection
            if (MapId.HasValue && mapsDict.TryGetValue(MapId.Value.ToString(), out var routedMap))
            {
                // URL parameter takes precedence
                MapNavigation.ChangeMap(routedMap.ID);
                state.CurrentMapId = routedMap.ID;
            }
            else if (config.MainMapId.HasValue && mapsDict.TryGetValue(config.MainMapId.Value.ToString(), out var mainMap))
            {
                // Select main map if configured
                MapNavigation.ChangeMap(mainMap.ID);
                state.CurrentMapId = mainMap.ID;
            }
            else if (mapsList.Count > 0)
            {
                // Fall back to first map in sorted list
                MapNavigation.ChangeMap(mapsList[0].ID);
                state.CurrentMapId = mapsList[0].ID;
            }

            // Load custom markers for the current map
            if (hasMarkersPermission && MapNavigation.CurrentMapId > 0)
            {
                await LoadCustomMarkersAsync();
            }

            // Load timers
            await LoadTimersAsync();

            // NOTE: Circuit event subscription moved to OnAfterRenderAsync to avoid race conditions during init

            // Subscribe to breakpoint changes (don't fire immediately to avoid illegal StateHasChanged during init)
            await BrowserViewportService.SubscribeAsync(this, fireImmediately: false);

            // Manually set initial sidebar state based on current breakpoint
            var currentBreakpoint = await BrowserViewportService.GetCurrentBreakpointAsync();
            isSidebarOpen = currentBreakpoint >= Breakpoint.Lg;
            // No StateHasChanged needed - we're already in OnInitializedAsync render cycle

            // Set up polling timer for markers
            markerUpdateTimer = new Timer(UpdateMarkers, null, 60000, 60000);

            isLoaded = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                                              ex.StatusCode == System.Net.HttpStatusCode.Found)
        {
            Logger.LogError(ex, "Authentication failed during map initialization");
            Snackbar.Add("Authentication failed. Please refresh the page.", Severity.Error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing map");
            Snackbar.Add("Failed to load map. Please refresh the page.", Severity.Error);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Wrap entire method in try-catch to prevent circuit crashes from killing the app
        // This is critical because user interactions (drag, zoom) can fire before circuit is ready
        try
        {
            // ONE-TIME EVENT SUBSCRIPTIONS: Runs exactly once on first render
            // These must use firstRender to ensure they only happen once
            if (firstRender)
            {
                // Subscribe to circuit reconnection events BEFORE SSE initialization
                // This ensures event handlers are registered before SSE callbacks can fire
                ReconnectionState.OnDown += HandleCircuitDown;
                ReconnectionState.OnUp += HandleCircuitUp;

                // Wait for circuit to fully stabilize before connecting SSE
                // This prevents "No interop methods are registered for renderer" errors
                // when SSE events fire during circuit initialization
                await Task.Delay(100);

                // Mark circuit as fully ready for JS->NET calls
                circuitFullyReady = true;

                // Load hidden marker groups from localStorage
                await LoadHiddenMarkerGroupsAsync();

                // Load floating button toggle states from localStorage
                await LoadToggleStatesAsync();

                // Load "my character" name from localStorage
                await LoadMyCharacterAsync();

                // NOTE: SSE initialization moved to HandleMapInitialized
                // because mapView component reference is not available until Leaflet fires its 'load' event
            }

            // NOTE: All initialization logic has been moved to event-driven handlers:
            // - Map positioning: HandleMapInitialized (triggered by Leaflet 'load' event)
            // - Marker loading: HandleMapInitialized (after positioning completes)
            // This eliminates render-cycle polling and guarantees proper initialization order
        }
        catch (Exception ex)
        {
            // Log error but don't crash the circuit
            // This prevents user interactions during init from killing the entire app
            Logger.LogError(ex, "Error during OnAfterRenderAsync - circuit will continue");
        }
    }

    /// <summary>
    /// Initializes map view position from URL parameters. Runs exactly once on first render.
    /// </summary>
    private async Task InitializeViewFromUrlParametersAsync()
    {
        Logger.LogInformation("InitializeViewFromUrlParametersAsync called - MapId: {MapId}, GridX: {GridX}, GridY: {GridY}, Zoom: {Zoom}, CharacterId: {CharacterId}",
            MapId, GridX, GridY, Zoom, CharacterId);

        // Handle route parameters (from URL query string for bookmarks)
        if (CharacterId.HasValue)
        {
            Logger.LogInformation("Following character: {CharacterId}", CharacterId.Value);
            state.TrackingCharacterId = CharacterId;
            await TrackCharacter(CharacterId.Value);
        }
        else if (MapId.HasValue && GridX.HasValue && GridY.HasValue && Zoom.HasValue)
        {
            var requestedMapId = MapId.Value;
            Logger.LogInformation("Setting position from URL params - Map: {MapId}, Position: ({X}, {Y}), Zoom: {Z}",
                requestedMapId, GridX.Value, GridY.Value, Zoom.Value);

            if (requestedMapId > 0)
            {
                MapNavigation.ChangeMap(requestedMapId);
                state.CurrentMapId = requestedMapId;
                await mapView.ChangeMapAsync(requestedMapId);
                await mapView.SetViewAsync(GridX.Value, GridY.Value, Zoom.Value);
                // Synchronize MapNavigationService state to prevent position resets
                MapNavigation.UpdatePosition(GridX.Value, GridY.Value, Zoom.Value);
                Logger.LogInformation("Position set to ({X}, {Y}, {Z}) successfully", GridX.Value, GridY.Value, Zoom.Value);
            }
        }
        else if (MapNavigation.Maps.Count > 0)
        {
            var firstMap = MapNavigation.Maps[0];
            var startX = firstMap.MapInfo.DefaultStartX ?? 0;
            var startY = firstMap.MapInfo.DefaultStartY ?? 0;
            Logger.LogInformation("No URL params, using default position ({X}, {Y}, 7) on map {MapId}", startX, startY, firstMap.ID);
            MapNavigation.ChangeMap(firstMap.ID);
            state.CurrentMapId = firstMap.ID;
            await mapView.ChangeMapAsync(firstMap.ID);
            await mapView.SetViewAsync(startX, startY, 7);
            // Synchronize MapNavigationService state to prevent position resets
            MapNavigation.UpdatePosition(startX, startY, 7);
        }
        else
        {
            Logger.LogWarning("No maps available, cannot initialize position");
        }

        // Clear query string parameters after initial use to prevent them from being re-applied on subsequent renders
        MapId = null;
        GridX = null;
        GridY = null;
        Zoom = null;
        CharacterId = null;
    }

    /// <summary>
    /// Loads markers for the current map. This is separated from initialization to prevent
    /// marker-related re-renders from affecting map positioning.
    /// Called from HandleMapInitialized after map is ready (event-driven, no delays needed).
    /// </summary>
    private async Task LoadMarkersForCurrentMapAsync()
    {
        // Ensure JavaScript map is set before loading markers to prevent filtering mismatches
        if (MapNavigation.CurrentMapId > 0 && mapView != null)
        {
            await mapView.ChangeMapAsync(MapNavigation.CurrentMapId);

            // No delay needed - this is called from HandleMapInitialized after Leaflet 'load' event
            // The map is guaranteed to be ready at this point (event-driven architecture)

            // Load markers for current map (only if markers layer is enabled)
            if (LayerVisibility.ShowMarkers)
            {
                var markersToLoad = MarkerState.GetMarkersForMap(MapNavigation.CurrentMapId).ToList();

                // Enrich markers with timer data before adding to map
                EnrichMarkersWithTimerData(markersToLoad);

                // Batch add all markers in a single JS interop call for performance
                await mapView.AddMarkersAsync(markersToLoad);
            }

            // Load custom markers
            await TryRenderPendingCustomMarkersAsync();

            // Load roads
            await RefreshRoadsAsync();
        }

        // Note: No StateHasChanged() here - marker loading should NOT trigger component re-render
        // Markers are rendered directly via JavaScript interop
    }

    public async ValueTask DisposeAsync()
    {
        ReconnectionState.OnDown -= HandleCircuitDown;
        ReconnectionState.OnUp -= HandleCircuitUp;

        await BrowserViewportService.UnsubscribeAsync(this);

        markerUpdateTimer?.Dispose();
        permissionCheckTimer?.Dispose();

        if (sseModule != null)
        {
            try
            {
                await sseModule.InvokeVoidAsync("disconnectSseUpdates");
                await sseModule.DisposeAsync();
            }
            catch { }
            sseModule = null;
        }

        if (leafletModule != null)
        {
            try
            {
                await leafletModule.DisposeAsync();
            }
            catch { }
            leafletModule = null;
        }

        sseDotnetRef?.Dispose();
        sseDotnetRef = null;

        if (mapView != null)
        {
            await mapView.DisposeAsync();
        }
    }

    #endregion

    #region Polling Methods

    private async void UpdateMarkers(object? timerState)
    {
        try
        {
            if (!AuthStateCache.IsAuthenticated || string.IsNullOrEmpty(AuthStateCache.CookieValue))
            {
                // Retry with exponential backoff instead of immediately failing
                if (markerUpdateRetryCount < MaxMarkerUpdateRetries)
                {
                    markerUpdateRetryCount++;
                    var delaySeconds = Math.Pow(2, markerUpdateRetryCount); // 2s, 4s, 8s
                    Logger.LogWarning(
                        "Authentication state not available for marker update. Retry {RetryCount}/{MaxRetries} in {DelaySeconds}s.",
                        markerUpdateRetryCount, MaxMarkerUpdateRetries, delaySeconds);

                    // Reschedule timer with exponential backoff delay
                    markerUpdateTimer?.Change(TimeSpan.FromSeconds(delaySeconds), TimeSpan.FromMilliseconds(-1));
                    return;
                }

                // All retries exhausted - stop timer and show error
                Logger.LogWarning("Authentication state not available after {RetryCount} retries. Stopping timer.", MaxMarkerUpdateRetries);
                await InvokeAsync(() =>
                {
                    Snackbar.Add("Authentication lost. Marker updates stopped. Please refresh the page.", Severity.Warning);
                    StateHasChanged();
                });
                markerUpdateTimer?.Dispose();
                markerUpdateTimer = null;
                return;
            }

            var markers = await MapData.GetMarkersAsync();

            // Reset retry counter on successful update
            markerUpdateRetryCount = 0;

            await InvokeAsync(async () =>
            {
                var result = MarkerState.UpdateFromApi(markers, MapNavigation.CurrentMapId);

                // Enrich markers with timer data before updating
                EnrichMarkersWithTimerData(result.AddedMarkers);
                EnrichMarkersWithTimerData(result.UpdatedMarkers);

                // Update map view
                if (mapView != null)
                {
                    // Batch add new markers for performance
                    if (result.AddedMarkers.Count > 0)
                    {
                        await mapView.AddMarkersAsync(result.AddedMarkers);
                    }

                    foreach (var marker in result.UpdatedMarkers)
                    {
                        await mapView.UpdateMarkerAsync(marker);
                    }

                    foreach (var markerId in result.RemovedMarkerIds)
                    {
                        await mapView.RemoveMarkerAsync(markerId);
                    }
                }

                StateHasChanged();
            });

            // Reset timer to normal 60s interval after successful update
            markerUpdateTimer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                                              ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                                              ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                              ex.StatusCode == System.Net.HttpStatusCode.Found)
        {
            // Retry with exponential backoff for HTTP auth errors
            if (markerUpdateRetryCount < MaxMarkerUpdateRetries)
            {
                markerUpdateRetryCount++;
                var delaySeconds = Math.Pow(2, markerUpdateRetryCount); // 2s, 4s, 8s
                Logger.LogWarning(
                    "Authentication failed while updating markers (Status: {StatusCode}). Retry {RetryCount}/{MaxRetries} in {DelaySeconds}s.",
                    ex.StatusCode, markerUpdateRetryCount, MaxMarkerUpdateRetries, delaySeconds);

                // Reschedule timer with exponential backoff delay
                markerUpdateTimer?.Change(TimeSpan.FromSeconds(delaySeconds), TimeSpan.FromMilliseconds(-1));
                return;
            }

            // All retries exhausted - stop timer and show error
            Logger.LogWarning("Authentication failed after {RetryCount} retries (Status: {StatusCode}). Stopping timer.", MaxMarkerUpdateRetries, ex.StatusCode);
            await InvokeAsync(() =>
            {
                var message = ex.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "Authentication failed. Marker updates stopped. Please refresh the page.",
                    System.Net.HttpStatusCode.Found => "Authentication session expired. Marker updates stopped. Please refresh the page.",
                    _ => "Failed to update markers. Updates stopped."
                };
                Snackbar.Add(message, Severity.Warning);
                StateHasChanged();
            });
            markerUpdateTimer?.Dispose();
            markerUpdateTimer = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating markers");
            await InvokeAsync(() =>
            {
                Snackbar.Add("Error updating markers. Check console for details.", Severity.Error);
                StateHasChanged();
            });
        }
    }

    private async void CheckForMapPermission(object? state)
    {
        try
        {
            Logger.LogDebug("Checking for Map permission update...");
            var config = await MapData.GetConfigAsync();
            var nowHasMapPermission = config.Permissions.Any(a => a.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));

            if (nowHasMapPermission && !hasMapPermission)
            {
                Logger.LogInformation("Map permission granted! Reloading page.");
                permissionCheckTimer?.Dispose();
                permissionCheckTimer = null;

                await InvokeAsync(() =>
                {
                    Snackbar.Add("Map access granted! Reloading...", Severity.Success);
                    Navigation.NavigateTo("/map", forceLoad: true);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking for map permission");
        }
    }

    #endregion

    #region Event Handlers - Map Interaction

    private async Task HandleMapDragged((int x, int y, int z) coords)
    {
        // Check if circuit is fully ready before updating URL
        // Prevents "Cannot send data" errors during circuit initialization/reconnection
        if (!circuitFullyReady)
        {
            Logger.LogDebug("HandleMapDragged called before circuit ready, dropping URL update");
            return;  // Drop this update - another drag event will arrive soon
        }

        if (IsFollowing)
        {
            StopFollowing();
        }

        try
        {
            MapNavigation.UpdatePosition(coords.x, coords.y, coords.z);
            Navigation.NavigateTo(MapNavigation.GetUrl(MapNavigation.CurrentMapId, coords.x, coords.y, coords.z), false);
            state.TrackingCharacterId = null;
        }
        catch (Exception ex)
        {
            // Log navigation errors but don't crash - another drag event will retry
            Logger.LogWarning(ex, "Failed to update URL during map drag to ({X},{Y},{Z})", coords.x, coords.y, coords.z);
        }
    }

    private async Task HandleMapZoomed((int x, int y, int z) coords)
    {
        // Check if circuit is fully ready before updating URL
        // Prevents "Cannot send data" errors during circuit initialization/reconnection
        if (!circuitFullyReady)
        {
            Logger.LogDebug("HandleMapZoomed called before circuit ready, dropping URL update");
            return;  // Drop this update - another zoom event will arrive soon
        }

        if (IsFollowing)
        {
            StopFollowing();
        }

        try
        {
            MapNavigation.UpdatePosition(coords.x, coords.y, coords.z);
            Navigation.NavigateTo(MapNavigation.GetUrl(MapNavigation.CurrentMapId, coords.x, coords.y, coords.z), false);
        }
        catch (Exception ex)
        {
            // Log navigation errors but don't crash - another zoom event will retry
            Logger.LogWarning(ex, "Failed to update URL during map zoom to ({X},{Y},{Z})", coords.x, coords.y, coords.z);
        }
    }

    private async Task HandleMapInitialized(bool isReady)
    {
        Logger.LogInformation("Map initialized event received, starting initialization sequence");

        // Set initial revisions for all maps
        foreach (var map in MapNavigation.Maps)
        {
            await mapView.SetMapRevisionAsync(map.ID, map.MapInfo.Revision);
            MapNavigation.InitializeMapRevision(map.ID, map.MapInfo.Revision);
            Logger.LogDebug("Set initial revision for map {MapId}: {Revision}", map.ID, map.MapInfo.Revision);
        }

        // Handle route parameters and set initial view position
        await InitializeViewFromUrlParametersAsync();

        // Load active timers
        await LoadTimersAsync();
        StateHasChanged();

        // Load markers after positioning is complete
        // This is event-driven (triggered by Leaflet 'load') instead of render-cycle polling
        if (mapView != null && MapNavigation.CurrentMapId > 0)
        {
            // Sync hidden marker groups to JS before loading markers
            if (hiddenMarkerGroups.Count > 0)
            {
                await mapView.SetHiddenMarkerTypesAsync(hiddenMarkerGroups);
            }

            Logger.LogInformation("Loading markers for map {MapId}", MapNavigation.CurrentMapId);
            await LoadMarkersForCurrentMapAsync();

            // Sync character tooltip visibility with LayerVisibilityService default state
            await mapView.ToggleCharacterTooltipsAsync(LayerVisibility.ShouldShowCharacterTooltips());
        }

        // Initialize SSE after map is ready - this is the correct place because:
        // 1. mapView component reference is now guaranteed to be available
        // 2. Leaflet 'load' event has fired, so all JS interop is ready
        // 3. circuitFullyReady should already be true from OnAfterRenderAsync
        await InitializeBrowserSseAsync();

        // Apply loaded toggle states to JavaScript now that map is fully ready
        await ApplyToggleStatesToJavaScriptAsync();
    }

    private Task HandleMapChanged(int mapId)
    {
        Logger.LogInformation("Map changed to {MapId}", mapId);
        // Map change is complete - markers are already filtered by map in JavaScript
        // No additional action needed here for now
        return Task.CompletedTask;
    }

    private async Task HandleContextMenu((int gridX, int gridY, int screenX, int screenY) coords)
    {
        contextCoords = (coords.gridX, coords.gridY);
        contextMenuX = coords.screenX;
        contextMenuY = coords.screenY;
        showContextMenu = true;
        showMarkerContextMenu = false;
        StateHasChanged();
    }

    private async Task HandleMapRightClick((int mapId, int coordX, int coordY, int x, int y, int screenX, int screenY) data)
    {
        // Store coordinates for menu actions
        mapActionMapId = data.mapId;
        mapActionCoordX = data.coordX;
        mapActionCoordY = data.coordY;
        mapActionX = data.x;
        mapActionY = data.y;
        contextMenuX = data.screenX;
        contextMenuY = data.screenY;

        // If in drawing mode, fetch the current points count for the menu
        if (isDrawingRoad)
        {
            try
            {
                leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
                drawingPointsCount = await leafletModule.InvokeAsync<int>("getDrawingPointsCount");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to get drawing points count");
                drawingPointsCount = 0;
            }
        }

        // Show map action menu
        showMapActionMenu = true;
        showContextMenu = false;
        showMarkerContextMenu = false;

        await InvokeAsync(StateHasChanged);
    }

    private void HandleMapClick()
    {
        if (showContextMenu || showMarkerContextMenu || showCustomMarkerContextMenu || showMapActionMenu || showRoadContextMenu)
        {
            showContextMenu = false;
            showMarkerContextMenu = false;
            showCustomMarkerContextMenu = false;
            showMapActionMenu = false;
            showRoadContextMenu = false;
            StateHasChanged();
        }
    }

    private void HideAllContextMenus()
    {
        showContextMenu = false;
        showMarkerContextMenu = false;
        showCustomMarkerContextMenu = false;
        showMapActionMenu = false;
        showRoadContextMenu = false;
    }

    private async Task HandleGoToPing()
    {
        if (mapView != null)
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            var success = await leafletModule.InvokeAsync<bool>("jumpToLatestPing");

            if (!success)
            {
                Snackbar.Add("No pings available", Severity.Info);
            }
        }
    }

    private async Task HandleMarkerClicked(int markerId)
    {
        if (mapView != null)
        {
            await mapView.JumpToMarkerAsync(markerId);
        }
    }

    private async Task HandleMarkerContextMenu((int markerId, int screenX, int screenY) data)
    {
        contextMarkerId = data.markerId;
        contextMenuX = data.screenX;
        contextMenuY = data.screenY;

        // Check if this marker has an active timer
        var now = DateTime.UtcNow;
        markerHasTimer = allTimers.Any(t =>
            t.Type == "Marker" &&
            t.MarkerId == data.markerId &&
            !t.IsCompleted &&
            t.ReadyAt > now);

        showMarkerContextMenu = true;
        showContextMenu = false;
        showCustomMarkerContextMenu = false;
        StateHasChanged();
    }

    private async Task HandleCustomMarkerContextMenu((int customMarkerId, int screenX, int screenY) data)
    {
        contextCustomMarkerId = data.customMarkerId;
        contextMenuX = data.screenX;
        contextMenuY = data.screenY;

        // Check if this custom marker has an active timer
        var now = DateTime.UtcNow;
        customMarkerHasTimer = allTimers.Any(t =>
            t.Type == "CustomMarker" &&
            t.CustomMarkerId == data.customMarkerId &&
            !t.IsCompleted &&
            t.ReadyAt > now);

        showCustomMarkerContextMenu = true;
        showContextMenu = false;
        showMarkerContextMenu = false;
        StateHasChanged();
    }

    #endregion

    #region Event Handlers - Map Controls

    private async Task HandleToggleGridCoordinates()
    {
        LayerVisibility.ToggleGridCoordinates();
        state.ShowGridCoordinates = LayerVisibility.ShowGridCoordinates;
        if (mapView != null)
        {
            await mapView.ToggleGridCoordinatesAsync(LayerVisibility.ShowGridCoordinates);
        }
    }

    private async Task HandleZoomOut()
    {
        if (mapView != null)
        {
            await mapView.ZoomOutAsync();
        }
    }

    private async Task HandleZoomIn()
    {
        if (mapView != null)
        {
            var currentZoom = MapNavigation.Zoom;
            if (currentZoom < 7)
            {
                await mapView.SetViewAsync((int)MapNavigation.CenterX, (int)MapNavigation.CenterY, currentZoom + 1);
            }
        }
    }

    private async Task HandleResetView()
    {
        if (mapView != null && MapNavigation.Maps.Count > 0)
        {
            // Find the current map to get its default starting position
            var currentMap = MapNavigation.Maps.FirstOrDefault(m => m.ID == MapNavigation.CurrentMapId);
            var startX = currentMap?.MapInfo.DefaultStartX ?? 0;
            var startY = currentMap?.MapInfo.DefaultStartY ?? 0;

            await mapView.ChangeMapAsync(MapNavigation.CurrentMapId);
            await mapView.SetViewAsync(startX, startY, 7);
            MapNavigation.UpdatePosition(startX, startY, 7);
            Navigation.NavigateTo(MapNavigation.GetUrl(MapNavigation.CurrentMapId, startX, startY, 7), false);
        }
    }

    private async Task HandleMapSelected(MapInfoModel? map)
    {
        if (map != null && mapView != null)
        {
            MapNavigation.ChangeMap(map.ID);
            state.CurrentMapId = map.ID;
            await mapView.ChangeMapAsync(map.ID);

            await RebuildMarkersForCurrentMap();

            // Use the map's default starting position, or (0, 0) if not set
            var startX = map.MapInfo.DefaultStartX ?? 0;
            var startY = map.MapInfo.DefaultStartY ?? 0;
            await mapView.SetViewAsync(startX, startY, 7);
            MapNavigation.UpdatePosition(startX, startY, 7);
            Navigation.NavigateTo(MapNavigation.GetUrl(map.ID, startX, startY, 7), false);

            CustomMarkerState.MarkAsNeedingRender();
            await TryRenderPendingCustomMarkersAsync();
            StateHasChanged();
        }
    }

    private async Task HandleOverlayMapSelected(MapInfoModel? map)
    {
        if (mapView != null)
        {
            MapNavigation.ChangeOverlayMap(map?.ID);

            // Load saved offset from API when overlay map is selected
            if (map != null && MapNavigation.CurrentMapId > 0)
            {
                try
                {
                    var httpClient = HttpClientFactory.CreateClient("API");
                    var response = await httpClient.GetAsync(
                        $"/map/api/v1/overlay-offset?currentMapId={MapNavigation.CurrentMapId}&overlayMapId={map.ID}");

                    if (response.IsSuccessStatusCode)
                    {
                        var offset = await response.Content.ReadFromJsonAsync<OverlayOffsetResponse>(CamelCaseJsonOptions);
                        if (offset != null)
                        {
                            MapNavigation.OverlayOffsetX = offset.OffsetX;
                            MapNavigation.OverlayOffsetY = offset.OffsetY;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load overlay offset for maps {CurrentMapId}/{OverlayMapId}",
                        MapNavigation.CurrentMapId, map.ID);
                }
            }

            await mapView.SetOverlayMapAsync(map?.ID, MapNavigation.OverlayOffsetX, MapNavigation.OverlayOffsetY);
        }
    }

    // DTO for overlay offset response
    private record OverlayOffsetResponse(int CurrentMapId, int OverlayMapId, double OffsetX, double OffsetY);

    private async Task HandleMapSelectedInternal(MapInfoModel? map)
    {
        if (map != null)
        {
            SelectedMap = map;
            await HandleMapSelected(map);
        }
    }

    private async Task HandleOverlayMapSelectedInternal(MapInfoModel? map)
    {
        OverlayMap = map;
        await HandleOverlayMapSelected(map);
    }

    private async Task HandleOverlayOffsetXChanged(double offsetX)
    {
        MapNavigation.OverlayOffsetX = offsetX;
        if (mapView != null)
        {
            await mapView.SetOverlayOffsetAsync(MapNavigation.OverlayOffsetX, MapNavigation.OverlayOffsetY);
        }
    }

    private async Task HandleOverlayOffsetYChanged(double offsetY)
    {
        MapNavigation.OverlayOffsetY = offsetY;
        if (mapView != null)
        {
            await mapView.SetOverlayOffsetAsync(MapNavigation.OverlayOffsetX, MapNavigation.OverlayOffsetY);
        }
    }

    private async Task HandleSaveOverlayOffset()
    {
        await SaveOverlayOffsetAsync();
    }

    private async Task SaveOverlayOffsetAsync()
    {
        if (MapNavigation.OverlayMapId is not { } overlayMapId || overlayMapId <= 0)
            return;

        if (MapNavigation.CurrentMapId <= 0)
            return;

        try
        {
            var httpClient = HttpClientFactory.CreateClient("API");
            var request = new
            {
                CurrentMapId = MapNavigation.CurrentMapId,
                OverlayMapId = overlayMapId,
                OffsetX = MapNavigation.OverlayOffsetX,
                OffsetY = MapNavigation.OverlayOffsetY
            };

            await httpClient.PostAsJsonAsync("/map/api/v1/overlay-offset", request);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save overlay offset");
        }
    }

    private async Task HandleToggleGridCoordinates(bool value)
    {
        LayerVisibility.ShowGridCoordinates = value;
        state.ShowGridCoordinates = value;
        if (mapView != null)
        {
            await mapView.ToggleGridCoordinatesAsync(value);
        }
    }

    #endregion

    #region Event Handlers - Player/Marker Selection

    private async Task SelectPlayer(CharacterModel character)
    {
        CharacterTracking.StartFollowing(character.Id);
        state.TrackingCharacterId = character.Id;

        await CenterOnCharacterAsync(character, switchMap: true);
        await CloseSidebarOnMobileAsync();
    }

    private async Task SelectMarker(MarkerModel marker)
    {
        if (mapView != null)
        {
            if (MapNavigation.CurrentMapId != marker.Map)
            {
                MapNavigation.ChangeMap(marker.Map);
                state.CurrentMapId = marker.Map;
                await mapView.ChangeMapAsync(marker.Map);

                await RebuildMarkersForCurrentMap();

                Navigation.NavigateTo(MapNavigation.GetUrl(marker.Map, 0, 0, MapNavigation.Zoom), false);

                CustomMarkerState.MarkAsNeedingRender();
                await TryRenderPendingCustomMarkersAsync();
            }

            await mapView.JumpToMarkerAsync(marker.Id);
            await CloseSidebarOnMobileAsync();
        }
    }

    private void StopFollowing()
    {
        CharacterTracking.StopFollowing();
        state.TrackingCharacterId = null;
        Snackbar.Add("Stopped following player", Severity.Info, config => config.VisibleStateDuration = 1500);
    }

    private async Task HandleCharacterSelected(CharacterModel character)
    {
        await SelectPlayer(character);
    }

    private async Task HandleMarkerSelected(MarkerModel marker)
    {
        if (mapView != null)
        {
            await mapView.JumpToMarkerAsync(marker.Id);
        }
    }

    private async Task SelectCustomMarker(CustomMarkerViewModel marker)
    {
        await CloseSidebarOnMobileAsync();

        if (mapView != null)
        {
            Logger.LogInformation("Selecting custom marker {MarkerId} ({Title}) on map {MapId}", marker.Id, marker.Title, marker.MapId);

            if (MapNavigation.CurrentMapId != marker.MapId)
            {
                MapNavigation.ChangeMap(marker.MapId);
                state.CurrentMapId = marker.MapId;
                await mapView.ChangeMapAsync(marker.MapId);

                await RebuildMarkersForCurrentMap();

                Navigation.NavigateTo(MapNavigation.GetUrl(marker.MapId, 0, 0, MapNavigation.Zoom), false);

                CustomMarkerState.MarkAsNeedingRender();
                await TryRenderPendingCustomMarkersAsync();
            }
            else
            {
                await TryRenderPendingCustomMarkersAsync();
            }

            await JS.InvokeVoidAsync("console.log", $"[CustomMarker] .NET requesting jump to marker {marker.Id} ({marker.Title})");
            await mapView.JumpToCustomMarkerAsync(marker.Id, 6);
        }
    }

    #endregion

    #region Event Handlers - Layer Toggles

    private async Task HandleLayerToggle(Action action)
    {
        action();

        // Sync LayerVisibility service back to state object for UI binding
        state.ShowPlayers = LayerVisibility.ShowPlayers;
        state.ShowPlayerTooltips = LayerVisibility.ShowPlayerTooltips;
        state.ShowMarkers = LayerVisibility.ShowMarkers;
        state.ShowCustomMarkers = LayerVisibility.ShowCustomMarkers;
        state.ShowThingwalls = LayerVisibility.ShowThingwalls;
        state.ShowThingwallTooltips = LayerVisibility.ShowThingwallTooltips;
        state.ShowQuests = LayerVisibility.ShowQuests;
        state.ShowQuestTooltips = LayerVisibility.ShowQuestTooltips;
        state.ShowClustering = LayerVisibility.ShowClustering;

        await SyncLayerVisibility();
        StateHasChanged();  // Force UI re-render
    }

    private async Task ToggleClustering()
    {
        showClustering = !showClustering;
        LayerVisibility.ShowClustering = showClustering;
        state.ShowClustering = showClustering;

        if (mapView != null)
        {
            await mapView.SetClusteringEnabled(showClustering);
        }

        await SaveToggleStatesAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleMarkerGroupVisibilityChanged((string ImageType, bool Visible) args)
    {
        if (args.Visible)
        {
            hiddenMarkerGroups.Remove(args.ImageType);
        }
        else
        {
            hiddenMarkerGroups.Add(args.ImageType);
        }

        // Save to localStorage
        await SaveHiddenMarkerGroupsAsync();

        // Sync to JavaScript
        if (mapView != null)
        {
            await mapView.SetHiddenMarkerTypesAsync(hiddenMarkerGroups);

            // Re-add markers that were previously hidden (if now visible)
            // The JS addMarker function skips duplicates, so this just adds newly visible markers
            if (args.Visible)
            {
                await LoadMarkersForCurrentMapAsync();
            }
        }

        StateHasChanged();
    }

    private async Task SaveHiddenMarkerGroupsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(hiddenMarkerGroups.ToList(), CamelCaseJsonOptions);
            await SafeJs.SetLocalStorageAsync(HiddenMarkerGroupsStorageKey, json);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save hidden marker groups to localStorage");
        }
    }

    private async Task LoadHiddenMarkerGroupsAsync()
    {
        try
        {
            var json = await SafeJs.GetLocalStorageAsync(HiddenMarkerGroupsStorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var groups = JsonSerializer.Deserialize<List<string>>(json, CamelCaseJsonOptions);
                if (groups != null)
                {
                    hiddenMarkerGroups = new HashSet<string>(groups);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load hidden marker groups from localStorage");
        }
    }

    private async Task SaveToggleStatesAsync()
    {
        try
        {
            var toggleStates = new Dictionary<string, bool>
            {
                ["showPClaim"] = showPClaim,
                ["showVClaim"] = showVClaim,
                ["showProvince"] = showProvince,
                ["showThingwallHighlight"] = showThingwallHighlight,
                ["showQuestGiverHighlight"] = showQuestGiverHighlight,
                ["showMarkerFilterMode"] = showMarkerFilterMode,
                ["showClustering"] = showClustering,
                ["showRoads"] = showRoads
            };
            var json = JsonSerializer.Serialize(toggleStates, CamelCaseJsonOptions);
            await SafeJs.SetLocalStorageAsync(ToggleStatesStorageKey, json);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save toggle states to localStorage");
        }
    }

    private async Task LoadToggleStatesAsync()
    {
        try
        {
            var json = await SafeJs.GetLocalStorageAsync(ToggleStatesStorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var toggleStates = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, CamelCaseJsonOptions);
                if (toggleStates != null)
                {
                    if (toggleStates.TryGetValue("showPClaim", out var pclaim)) showPClaim = pclaim;
                    if (toggleStates.TryGetValue("showVClaim", out var vclaim)) showVClaim = vclaim;
                    if (toggleStates.TryGetValue("showProvince", out var province)) showProvince = province;
                    if (toggleStates.TryGetValue("showThingwallHighlight", out var thingwall)) showThingwallHighlight = thingwall;
                    if (toggleStates.TryGetValue("showQuestGiverHighlight", out var quest)) showQuestGiverHighlight = quest;
                    if (toggleStates.TryGetValue("showMarkerFilterMode", out var filter)) showMarkerFilterMode = filter;
                    if (toggleStates.TryGetValue("showClustering", out var clustering)) showClustering = clustering;
                    if (toggleStates.TryGetValue("showRoads", out var roads)) showRoads = roads;

                    Logger.LogDebug("Loaded toggle states from localStorage");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load toggle states from localStorage");
        }
    }

    /// <summary>
    /// Applies loaded toggle states to JavaScript after map is initialized.
    /// Must be called after the map is ready for JS interop.
    /// </summary>
    private async Task ApplyToggleStatesToJavaScriptAsync()
    {
        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");

            // Apply PClaim toggle (default is false, so only apply if true)
            if (showPClaim)
            {
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "ClaimFloor", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "ClaimOutline", true);
            }

            // Apply VClaim toggle (default is false, so only apply if true)
            if (showVClaim)
            {
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "VillageFloor", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "VillageOutline", true);
            }

            // Apply Province toggle (default is false, so only apply if true)
            if (showProvince)
            {
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province0", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province1", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province2", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province3", true);
                await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province4", true);
            }

            // Apply Thingwall highlight toggle (default is false, so only apply if true)
            if (showThingwallHighlight)
            {
                await leafletModule.InvokeVoidAsync("setThingwallHighlightEnabled", true);
            }

            // Apply Quest giver highlight toggle (default is false, so only apply if true)
            if (showQuestGiverHighlight)
            {
                await leafletModule.InvokeVoidAsync("setQuestGiverHighlightEnabled", true);
            }

            // Apply Marker filter mode toggle (default is false, so only apply if true)
            if (showMarkerFilterMode)
            {
                await leafletModule.InvokeVoidAsync("setMarkerFilterModeEnabled", true);
            }

            // Apply Clustering toggle (default is true, so only apply if false)
            if (!showClustering && mapView != null)
            {
                await mapView.SetClusteringEnabled(false);
            }

            // Apply Roads toggle (default is true, so only apply if false)
            if (!showRoads)
            {
                await leafletModule.InvokeVoidAsync("toggleRoads", false);
            }

            Logger.LogDebug("Applied toggle states to JavaScript: PClaim={PClaim}, VClaim={VClaim}, Province={Province}, Thingwall={Thingwall}, Quest={Quest}, Filter={Filter}, Clustering={Clustering}, Roads={Roads}",
                showPClaim, showVClaim, showProvince, showThingwallHighlight, showQuestGiverHighlight, showMarkerFilterMode, showClustering, showRoads);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to apply toggle states to JavaScript");
        }
    }

    private async Task LoadMyCharacterAsync()
    {
        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            myCharacterName = await leafletModule.InvokeAsync<string?>("getMyCharacter");
            Logger.LogDebug("Loaded my character from localStorage: {Name}", myCharacterName ?? "(none)");

            // Sync to JavaScript for marker highlighting (gold glow + bigger size)
            if (!string.IsNullOrEmpty(myCharacterName))
            {
                await leafletModule.InvokeVoidAsync("setMyCharacterForHighlight", myCharacterName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load my character from localStorage");
        }
    }

    private async Task SetMyCharacterAsync(string? characterName)
    {
        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            await leafletModule.InvokeVoidAsync("setMyCharacter", characterName);
            myCharacterName = characterName;

            // Sync to JavaScript for marker highlighting (gold glow + bigger size)
            await leafletModule.InvokeVoidAsync("setMyCharacterForHighlight", characterName);

            if (characterName != null)
            {
                Snackbar.Add($"Set '{characterName}' as your character", Severity.Success);
            }
            else
            {
                Snackbar.Add("Cleared character selection", Severity.Info);
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save my character to localStorage");
            Snackbar.Add("Failed to save character selection", Severity.Error);
        }
    }

    /// <summary>
    /// Follow "my character" on the map - triggered by the FAB button
    /// </summary>
    private async Task FollowMyCharacterAsync()
    {
        if (string.IsNullOrEmpty(myCharacterName))
        {
            Snackbar.Add("Set your character first in the Players panel", Severity.Warning);
            return;
        }

        var myChar = CharacterTracking.AllCharacters.FirstOrDefault(c => c.Name == myCharacterName);
        if (myChar != null)
        {
            await SelectPlayer(myChar); // Existing method that starts following
        }
        else
        {
            Snackbar.Add($"'{myCharacterName}' is not online", Severity.Warning);
        }
    }

    /// <summary>
    /// Get button color for "Follow My Character" FAB
    /// </summary>
    private Color GetFollowMyCharacterButtonColor()
    {
        if (string.IsNullOrEmpty(myCharacterName)) return Color.Default;

        // Check if already following my character
        var myChar = CharacterTracking.AllCharacters.FirstOrDefault(c => c.Name == myCharacterName);
        if (myChar != null && followingCharacterId == myChar.Id) return Color.Success;

        return Color.Warning; // Gold/yellow to match the marker
    }

    private async Task HandleStateChanged()
    {
        await SyncLayerVisibility();
    }

    #endregion

    #region Event Handlers - Context Menu Actions

    private async Task HandleWipeTile()
    {
        showContextMenu = false;
        try
        {
            var success = await MapData.WipeTileAsync(MapNavigation.CurrentMapId, contextCoords.x, contextCoords.y);
            if (success)
            {
                Snackbar.Add($"Tile at ({contextCoords.x}, {contextCoords.y}) wiped successfully.", Severity.Success);
            }
            else
            {
                Snackbar.Add("Failed to wipe tile. Check console for details.", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error wiping tile");
            Snackbar.Add("Error wiping tile. Check console for details.", Severity.Error);
        }
    }

    private async Task HandleSetCoords()
    {
        showContextMenu = false;
        Logger.LogInformation("Set coords for tile {X}, {Y}", contextCoords.x, contextCoords.y);
    }

    private async Task HandleSetTimerForMarker(int markerId)
    {
        showMarkerContextMenu = false;

        // Find the marker in the current state
        var marker = MarkerState.AllMarkers.FirstOrDefault(m => m.Id == markerId);
        if (marker != null)
        {
            await ShowSetTimerForMarkerDialog(marker);
        }
        else
        {
            Logger.LogWarning("Marker {MarkerId} not found for timer creation", markerId);
            Snackbar.Add("Marker not found", Severity.Warning);
        }
    }

    private async Task HandlePingMarker(int markerId)
    {
        showMarkerContextMenu = false;

        // Find the marker
        var marker = MarkerState.AllMarkers.FirstOrDefault(m => m.Id == markerId);
        if (marker != null)
        {
            // Calculate grid coordinates from absolute position
            // Formula: absolute = local + grid * 100
            // For negative positions, we need Math.Floor to get the correct grid coordinate
            var coordX = (int)Math.Floor((double)marker.Position.X / 100);
            var coordY = (int)Math.Floor((double)marker.Position.Y / 100);

            // Local coordinates are the remainder, always in range [0, 100)
            var x = marker.Position.X - (coordX * 100);
            var y = marker.Position.Y - (coordY * 100);

            Logger.LogWarning("Creating ping for marker '{MarkerName}' (ID={MarkerId}) - Map={MapId}, AbsolutePos=({AbsX},{AbsY}), CalculatedGrid=({CoordX},{CoordY}), LocalPos=({X},{Y})",
                marker.Name, markerId, marker.Map, marker.Position.X, marker.Position.Y, coordX, coordY, x, y);

            // Create ping at marker location
            await JsCreatePing(marker.Map, coordX, coordY, x, y);
        }
        else
        {
            Logger.LogWarning("Marker {MarkerId} not found for ping", markerId);
            Snackbar.Add("Marker not found", Severity.Warning);
        }
    }

    private async Task HandleSetTimerForCustomMarker(int customMarkerId)
    {
        showCustomMarkerContextMenu = false;

        var customMarker = allCustomMarkers.FirstOrDefault(cm => cm.Id == customMarkerId);
        if (customMarker != null)
        {
            await ShowSetTimerForCustomMarkerDialog(customMarker);
        }
        else
        {
            Logger.LogWarning("Custom marker {CustomMarkerId} not found for timer creation", customMarkerId);
            Snackbar.Add("Custom marker not found", Severity.Warning);
        }
    }

    private async Task HandlePingCustomMarker(int customMarkerId)
    {
        showCustomMarkerContextMenu = false;

        var customMarker = allCustomMarkers.FirstOrDefault(cm => cm.Id == customMarkerId);
        if (customMarker != null)
        {
            // Create ping at custom marker location
            await JsCreatePing(customMarker.MapId, customMarker.CoordX, customMarker.CoordY, customMarker.X, customMarker.Y);
        }
        else
        {
            Logger.LogWarning("Custom marker {CustomMarkerId} not found for ping", customMarkerId);
            Snackbar.Add("Custom marker not found", Severity.Warning);
        }
    }

    private async Task HandleEditCustomMarker(int customMarkerId)
    {
        showCustomMarkerContextMenu = false;

        var customMarker = allCustomMarkers.FirstOrDefault(cm => cm.Id == customMarkerId);
        if (customMarker != null)
        {
            await ShowEditCustomMarkerDialog(customMarker);
        }
        else
        {
            Logger.LogWarning("Custom marker {CustomMarkerId} not found for edit", customMarkerId);
            Snackbar.Add("Custom marker not found", Severity.Warning);
        }
    }

    private async Task HandleDeleteCustomMarker(int customMarkerId)
    {
        showCustomMarkerContextMenu = false;

        var customMarker = allCustomMarkers.FirstOrDefault(cm => cm.Id == customMarkerId);
        if (customMarker != null)
        {
            await DeleteCustomMarkerAsync(customMarker);
        }
        else
        {
            Logger.LogWarning("Custom marker {CustomMarkerId} not found for deletion", customMarkerId);
            Snackbar.Add("Custom marker not found", Severity.Warning);
        }
    }

    #endregion

    #region Event Handlers - SSE Updates

    private void HandleTileUpdates(List<TileUpdate> updates)
    {
        if (mapView == null) return;

        _ = InvokeAsync(async () =>
        {
            try
            {
                if (mapView != null)
                {
                    foreach (var update in updates)
                    {
                        await mapView.RefreshTileAsync(update.M, update.X, update.Y, update.Z, update.T);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void HandleMapMerge(MapMerge merge)
    {
        InvokeAsync(async () =>
        {
            if (MapNavigation.CurrentMapId == merge.From && mapView != null)
            {
                Logger.LogInformation("Map merged from {From} to {To}", merge.From, merge.To);
                Snackbar.Add($"Map {merge.From} was merged into map {merge.To}. Switching view.", Severity.Info);

                var preserveCenterX = (int)MapNavigation.CenterX;
                var preserveCenterY = (int)MapNavigation.CenterY;
                var preserveZoom = MapNavigation.Zoom;

                MapNavigation.ChangeMap(merge.To);
                state.CurrentMapId = merge.To;
                await mapView.ChangeMapAsync(merge.To);

                var markers = await MapData.GetMarkersAsync();
                MarkerState.SetMarkers(markers);

                await mapView.SetViewAsync(preserveCenterX, preserveCenterY, preserveZoom);
                CustomMarkerState.MarkAsNeedingRender();
                await TryRenderPendingCustomMarkersAsync();
                StateHasChanged();
            }
        });
    }

    private void HandleMapUpdated(MapInfoModel updatedMap)
    {
        InvokeAsync(() =>
        {
            var existingMap = MapNavigation.GetMapById(updatedMap.ID);
            if (existingMap != null)
            {
                var wasHidden = existingMap.MapInfo.Hidden;
                MapNavigation.AddOrUpdateMap(updatedMap);

                if (SelectedMap?.ID == updatedMap.ID)
                {
                    SelectedMap = updatedMap;
                }

                if (OverlayMap?.ID == updatedMap.ID)
                {
                    OverlayMap = updatedMap;
                }

                if (!wasHidden && updatedMap.MapInfo.Hidden && MapNavigation.CurrentMapId == updatedMap.ID)
                {
                    var firstVisible = MapNavigation.GetFirstVisibleMap();
                    if (firstVisible != null && mapView != null)
                    {
                        Logger.LogInformation("Current map {MapId} became hidden, switching to {NewMapId}", updatedMap.ID, firstVisible.ID);
                        Snackbar.Add($"Map '{updatedMap.MapInfo.Name}' is now hidden. Switched to '{firstVisible.MapInfo.Name}'.", Severity.Info);
                        MapNavigation.ChangeMap(firstVisible.ID);
                        state.CurrentMapId = firstVisible.ID;
                        SelectedMap = firstVisible;
                        _ = mapView.ChangeMapAsync(firstVisible.ID);
                    }
                }

                Logger.LogInformation("Map {MapId} updated: {Name}, Hidden={Hidden}, Priority={Priority}",
                    updatedMap.ID, updatedMap.MapInfo.Name, updatedMap.MapInfo.Hidden, updatedMap.MapInfo.Priority);
                StateHasChanged();
            }
        });
    }

    private void HandleMapRevision(int mapId, int revision)
    {
        InvokeAsync(async () =>
        {
            if (mapView != null && !isReconnecting)
            {
                await mapView.SetMapRevisionAsync(mapId, revision);
                MapNavigation.SetMapRevision(mapId, revision);
                Logger.LogDebug("Map {MapId} revision updated to {Revision}", mapId, revision);

                if (MapNavigation.CurrentMapId == mapId)
                {
                    StateHasChanged();
                }
            }
        });
    }

    #endregion

    #region SSE JavaScript Callbacks

    [JSInvokable]
    public void OnSseTileUpdates(List<TileUpdate> updates)
    {
        HandleTileUpdates(updates);
    }

    [JSInvokable]
    public void OnSseMapMerge(MapMerge merge)
    {
        HandleMapMerge(merge);
    }

    [JSInvokable]
    public void OnSseMapUpdated(MapInfoModel updatedMap)
    {
        HandleMapUpdated(updatedMap);
    }

    [JSInvokable]
    public void OnSseMapDeleted(int deletedMapId)
    {
        HandleMapDeleted(deletedMapId);
    }

    [JSInvokable]
    public void OnSseMapRevision(int mapId, int revision)
    {
        HandleMapRevision(mapId, revision);
    }

    [JSInvokable]
    public async Task OnSseCharactersSnapshot(List<CharacterModel> characters)
    {
        // Check if circuit is fully ready (prevents "No interop methods registered" errors)
        if (!circuitFullyReady)
        {
            Logger.LogWarning("OnSseCharactersSnapshot called before circuit ready, waiting briefly...");
            await Task.Delay(50);  // Brief wait for circuit to finish initialization
            if (!circuitFullyReady)
            {
                Logger.LogWarning("Circuit still not ready after delay, dropping characters snapshot");
                return;  // Drop this callback - another snapshot will arrive soon
            }
        }

        Logger.LogDebug("Received SSE characters snapshot with {Count} characters", characters.Count);

        // Serialize SSE callbacks to prevent concurrent character state updates
        await sseCallbackLock.WaitAsync();
        try
        {
            await InvokeAsync(async () =>
            {
                try
                {
                    CharacterTracking.HandleCharactersSnapshot(characters);

                    if (mapView != null)
                    {
                        await mapView.SetCharactersSnapshotAsync(characters);

                        // Sync character tooltip visibility after loading characters
                        await mapView.ToggleCharacterTooltipsAsync(LayerVisibility.ShouldShowCharacterTooltips());
                    }
                    else
                    {
                        Logger.LogWarning("mapView is null in OnSseCharactersSnapshot, cannot update map");
                    }

                    StateHasChanged();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Exception processing SSE characters snapshot");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Exception in OnSseCharactersSnapshot");
        }
        finally
        {
            sseCallbackLock.Release();
        }
    }

    [JSInvokable]
    public async Task OnSseCharacterDelta(CharacterDeltaModel delta)
    {
        // Check if circuit is fully ready (prevents "No interop methods registered" errors)
        if (!circuitFullyReady)
        {
            Logger.LogDebug("OnSseCharacterDelta called before circuit ready, dropping delta");
            return;  // Drop this delta - another will arrive soon
        }

        // Serialize SSE callbacks to prevent concurrent character state updates
        await sseCallbackLock.WaitAsync();
        try
        {
            await InvokeAsync(async () =>
            {
                var result = CharacterTracking.HandleCharacterDelta(delta);

                if (mapView != null)
                {
                    await mapView.ApplyCharacterDeltaAsync(delta);
                }

                if (result.ShouldStopFollowing)
                {
                    StopFollowing();
                }

                // Follow mode: keep camera centered on followed character
                if (IsFollowing && FollowingCharacterId.HasValue && mapView != null)
                {
                    var followed = CharacterTracking.GetCharacterById(FollowingCharacterId.Value);
                    if (followed != null)
                    {
                        if (MapNavigation.CurrentMapId != followed.Map)
                        {
                            MapNavigation.ChangeMap(followed.Map);
                            state.CurrentMapId = followed.Map;
                            await mapView.ChangeMapAsync(followed.Map);
                            await RebuildMarkersForCurrentMap();
                        }

                        await mapView.JumpToCharacterAsync(followed.Id);
                    }
                }

                StateHasChanged();
            });
        }
        finally
        {
            sseCallbackLock.Release();
        }
    }

    private void HandleCircuitDown()
    {
        InvokeAsync(() =>
        {
            isReconnecting = true;

            markerUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            if (sseModule != null)
            {
                try
                {
                    _ = sseModule.InvokeVoidAsync("disconnectSseUpdates");
                }
                catch { }
            }

            Logger.LogInformation("Circuit disconnected - pausing updates");
            Snackbar.Add("Connection lost. Reconnecting...", Severity.Warning);

            StateHasChanged();
        });
    }

    private void HandleCircuitUp()
    {
        InvokeAsync(async () =>
        {
            isReconnecting = false;

            Logger.LogInformation("Circuit reconnected - rehydrating state");

            markerUpdateTimer?.Change(60000, 60000);

            if (sseModule != null && sseDotnetRef != null)
            {
                try
                {
                    await sseModule.InvokeVoidAsync("initializeSseUpdates", sseDotnetRef);
                    Logger.LogInformation("Browser SSE reconnected after circuit recovery");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to reconnect browser SSE after circuit recovery");
                }
            }

            if (mapView != null)
            {
                try
                {
                    await mapView.ChangeMapAsync(MapNavigation.CurrentMapId);

                    // Diagnostic: Log if this was attempting to restore view (could cause zoom resets)
                    Logger.LogWarning("Circuit reconnection - NOT restoring view position. Previous coords: ({X},{Y}) zoom {Zoom}",
                        MapNavigation.CenterX, MapNavigation.CenterY, MapNavigation.Zoom);

                    // REMOVED: await mapView.SetViewAsync(...) - This was causing random zoom resets
                    // User's current view position should be preserved by Leaflet across circuit reconnections

                    if (MapNavigation.OverlayMapId.HasValue)
                    {
                        await mapView.SetOverlayMapAsync(MapNavigation.OverlayMapId);
                    }

                    foreach (var map in MapNavigation.Maps)
                    {
                        await mapView.SetMapRevisionAsync(map.ID, map.MapInfo.Revision);
                    }

                    Logger.LogInformation("Map state rehydrated successfully");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to rehydrate map state after reconnect");
                }
            }

            CustomMarkerState.MarkAsNeedingRender();
            await TryRenderPendingCustomMarkersAsync();

            Snackbar.Add("Reconnected successfully", Severity.Success);
            StateHasChanged();
        });
    }

    private void HandleMapDeleted(int deletedMapId)
    {
        InvokeAsync(async () =>
        {
            var removedMap = MapNavigation.GetMapById(deletedMapId);
            if (removedMap != null)
            {
                MapNavigation.RemoveMap(deletedMapId);

                if (MapNavigation.CurrentMapId == deletedMapId)
                {
                    var firstMap = MapNavigation.GetFirstVisibleMap();
                    if (firstMap != null && mapView != null)
                    {
                        Logger.LogInformation("Current map {MapId} was deleted, switching to {NewMapId}", deletedMapId, firstMap.ID);
                        Snackbar.Add($"Map '{removedMap.MapInfo.Name}' was deleted. Switched to '{firstMap.MapInfo.Name}'.", Severity.Warning);
                        MapNavigation.ChangeMap(firstMap.ID);
                        state.CurrentMapId = firstMap.ID;
                        SelectedMap = firstMap;
                        await mapView.ChangeMapAsync(firstMap.ID);
                        CustomMarkerState.MarkAsNeedingRender();
                        await TryRenderPendingCustomMarkersAsync();
                    }
                    else
                    {
                        Snackbar.Add($"Map '{removedMap.MapInfo.Name}' was deleted. No maps available.", Severity.Warning);
                    }
                }

                if (OverlayMap?.ID == deletedMapId)
                {
                    Logger.LogInformation("Overlay map {MapId} was deleted, clearing overlay", deletedMapId);
                    OverlayMap = null;
                    MapNavigation.ChangeOverlayMap(null);
                    if (mapView != null)
                    {
                        await mapView.SetOverlayMapAsync(null);
                    }
                }

                Logger.LogInformation("Map {MapId} deleted from viewer", deletedMapId);
                StateHasChanged();
            }
        });
    }

    #endregion

    #region Helper Methods

    private string GetMapName(int mapId)
    {
        return MapNavigation.GetMapName(mapId);
    }

    private async Task CenterOnCharacterAsync(CharacterModel character, bool switchMap)
    {
        if (mapView == null) return;

        if (switchMap && MapNavigation.CurrentMapId != character.Map)
        {
            MapNavigation.ChangeMap(character.Map);
            state.CurrentMapId = character.Map;
            await mapView.ChangeMapAsync(character.Map);

            await mapView.ClearAllCharactersAsync();
            foreach (var ch in CharacterTracking.GetCharactersForMap(character.Map))
            {
                await mapView.AddCharacterAsync(ch);
            }

            // Sync character tooltip visibility after adding characters
            await mapView.ToggleCharacterTooltipsAsync(LayerVisibility.ShouldShowCharacterTooltips());

            await RebuildMarkersForCurrentMap();

            Navigation.NavigateTo(MapNavigation.GetUrl(character.Map, 0, 0, MapNavigation.Zoom), false);

            CustomMarkerState.MarkAsNeedingRender();
            await TryRenderPendingCustomMarkersAsync();
        }

        await mapView.JumpToCharacterAsync(character.Id);
    }

    private async Task TrackCharacter(int characterId)
    {
        if (mapView != null)
        {
            await mapView.JumpToCharacterAsync(characterId);
            Navigation.NavigateTo(MapNavigation.GetCharacterUrl(characterId), false);
        }
    }

    private async Task RebuildMarkersForCurrentMap()
    {
        if (mapView == null) return;

        await mapView.ClearAllMarkersAsync();

        var markers = MarkerState.GetMarkersForMap(MapNavigation.CurrentMapId).ToList();
        EnrichMarkersWithTimerData(markers);

        // Batch add all markers in a single JS interop call for performance
        await mapView.AddMarkersAsync(markers);
    }

    /// <summary>
    /// Synchronizes layer visibility state with the map view.
    /// Note: AddMarkerAsync is idempotent - JavaScript (marker-manager.js) handles duplicate adds gracefully.
    /// This method is called only when user explicitly toggles layer visibility, not on every render.
    /// </summary>
    private async Task SyncLayerVisibility()
    {
        if (mapView == null) return;

        await mapView.ToggleCharacterTooltipsAsync(LayerVisibility.ShouldShowCharacterTooltips());

        // Only sync markers for the current map (not all maps)
        foreach (var marker in MarkerState.GetMarkersForMap(MapNavigation.CurrentMapId))
        {
            bool shouldShow = LayerVisibility.ShouldShowMarker(marker);

            if (shouldShow)
            {
                // AddMarkerAsync is idempotent - marker-manager.js skips if marker already exists
                await mapView.AddMarkerAsync(marker);
            }
            else
            {
                await mapView.RemoveMarkerAsync(marker.Id);
            }
        }

        if (LayerVisibility.ShowThingwalls && LayerVisibility.ShowMarkers)
        {
            await mapView.ToggleMarkerTooltipsAsync("thingwall", LayerVisibility.ShowThingwallTooltips);
        }
        if (LayerVisibility.ShowQuests && LayerVisibility.ShowMarkers)
        {
            await mapView.ToggleMarkerTooltipsAsync("quest", LayerVisibility.ShowQuestTooltips);
        }
    }

    private void SetMode(SidebarMode mode)
    {
        sidebarMode = mode;
        StateHasChanged();
    }

    private string DrawerWidthCss => "min(400px, 85vw)";

    private string GetDrawerWidthForFab()
    {
        return "clamp(400px, 85vw, 100vw)";
    }

    private async Task CloseSidebarOnMobileAsync()
    {
        try
        {
            var currentBreakpoint = await BrowserViewportService.GetCurrentBreakpointAsync();

            if (currentBreakpoint < Breakpoint.Lg && isSidebarOpen)
            {
                isSidebarOpen = false;
                Logger.LogDebug("Closed sidebar on {Breakpoint} device after user interaction", currentBreakpoint);
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check breakpoint for sidebar auto-close");
        }
    }

    private string GetMenuButtonStyle()
    {
        var rightPosition = isSidebarOpen ? $"calc({DrawerWidthCss} + 16px)" : "16px";
        var visibility = isSidebarOpen ? "var(--fab-visibility, visible)" : "visible";

        return $"position: fixed; top: 80px; right: {rightPosition}; z-index: 1400; transition: right 0.3s ease; visibility: {visibility};";
    }

    private string GetControlsHintStyle()
    {
        var rightPosition = isSidebarOpen ? $"calc({DrawerWidthCss} + 16px)" : "16px";
        return $"position: fixed; bottom: 16px; right: {rightPosition}; z-index: 1400; transition: right 0.3s ease; max-width: 320px;";
    }

    #endregion

    #region Custom Markers

    private async Task LoadCustomMarkersAsync()
    {
        if (MapNavigation.CurrentMapId == 0) return;

        try
        {
            var httpClient = HttpClientFactory.CreateClient("API");
            var response = await httpClient.GetAsync($"/map/api/v1/custom-markers?mapId={MapNavigation.CurrentMapId}");

            if (response.IsSuccessStatusCode)
            {
                var markers = await response.Content.ReadFromJsonAsync<List<CustomMarkerViewModel>>(CamelCaseJsonOptions)
                    ?? new();
                CustomMarkerState.SetCustomMarkers(markers);
                Logger.LogDebug("Loaded {Count} custom markers for map {MapId}", markers.Count, MapNavigation.CurrentMapId);
            }
            else
            {
                Logger.LogWarning("Failed to load custom markers: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading custom markers");
        }
    }

    private async Task LoadTimersAsync()
    {
        try
        {
            var httpClient = HttpClientFactory.CreateClient("API");
            var response = await httpClient.GetAsync("/api/timers?includeCompleted=false&limit=1000");

            if (response.IsSuccessStatusCode)
            {
                allTimers = await response.Content.ReadFromJsonAsync<List<TimerDto>>(CamelCaseJsonOptions)
                    ?? new();
                Logger.LogDebug("Loaded {Count} active timers", allTimers.Count);
            }
            else
            {
                Logger.LogWarning("Failed to load timers: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading timers");
        }
    }

    private async Task RenderCustomMarkersAsync()
    {
        if (mapView == null) return;

        await mapView.ClearAllCustomMarkersAsync();

        var markersToRender = CustomMarkerState.GetCustomMarkersForMap(MapNavigation.CurrentMapId).ToList();

        // Enrich markers with timer data before rendering
        EnrichCustomMarkersWithTimerData(markersToRender);

        foreach (var marker in markersToRender)
        {
            await mapView.AddCustomMarkerAsync(marker);
        }

        CustomMarkerState.MarkAsRendered();
    }

    private async Task TryRenderPendingCustomMarkersAsync()
    {
        // Prevent concurrent execution - skip if already running
        if (!await customMarkerLock.WaitAsync(0))
        {
            Logger.LogDebug("Custom marker rendering already in progress, skipping concurrent call");
            return;
        }

        try
        {
            if (!CustomMarkerState.NeedsRendering(MapNavigation.CurrentMapId, LayerVisibility.ShowCustomMarkers))
            {
                return;
            }

            if (mapView == null)
            {
                Logger.LogDebug("MapView reference not yet available; pending render deferred.");
                return;
            }

            if (CustomMarkerState.AllCustomMarkers.Count == 0)
            {
                Logger.LogInformation("No custom markers loaded for map {MapId}; marking as rendered.", MapNavigation.CurrentMapId);
                CustomMarkerState.MarkAsRendered();
                return;
            }

            // Map is guaranteed to be initialized when called from HandleMapInitialized (event-driven)
            // This check is now just a safety guard for edge cases
            if (!mapView.IsInitialized)
            {
                Logger.LogWarning("MapView not initialized when rendering custom markers - this should not happen with event-driven architecture!");
                return;
            }

            Logger.LogInformation("Rendering {Count} custom markers for map {MapId}.", CustomMarkerState.AllCustomMarkers.Count, MapNavigation.CurrentMapId);

            if (MapNavigation.CurrentMapId > 0)
            {
                Logger.LogInformation("Activating map {MapId} prior to rendering custom markers.", MapNavigation.CurrentMapId);
                await mapView.ChangeMapAsync(MapNavigation.CurrentMapId);
            }

            await RenderCustomMarkersAsync();
        }
        finally
        {
            customMarkerLock.Release();
        }
    }

    private SemaphoreSlim _sseInitLock = new SemaphoreSlim(1, 1);

    private async Task InitializeBrowserSseAsync()
    {
        if (!await _sseInitLock.WaitAsync(0))
        {
            Logger.LogDebug("SSE initialization already in progress, skipping concurrent call");
            return;
        }

        try
        {
            if (sseInitialized)
            {
                Logger.LogDebug("SSE already initialized, skipping");
                return;
            }

            if (mapView == null)
            {
                Logger.LogWarning("mapView is null, cannot initialize SSE");
                return;
            }

            sseModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/map-updates.js{JsVersion}");
            sseDotnetRef ??= DotNetObjectReference.Create(this);
            await sseModule.InvokeVoidAsync("initializeSseUpdates", sseDotnetRef);
            Logger.LogInformation("SSE connection initialized successfully");
            sseInitialized = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize browser SSE");
            Snackbar.Add("Failed to connect to real-time updates. Map will still work, but updates may be delayed.", Severity.Warning);
        }
        finally
        {
            _sseInitLock.Release();
        }
    }

    private async Task RefreshCustomMarkersAsync()
    {
        await LoadCustomMarkersAsync();
        CustomMarkerState.MarkAsNeedingRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleMarkerCreatedAsync(CustomMarkerViewModel? marker)
    {
        if (marker == null)
        {
            await RefreshCustomMarkersAsync();
            return;
        }

        CustomMarkerState.AddOrUpdateCustomMarker(marker);

        if (marker.MapId == MapNavigation.CurrentMapId && LayerVisibility.ShowCustomMarkers && mapView != null)
        {
            // Enrich marker with timer data before adding to map
            EnrichCustomMarkersWithTimerData(new[] { marker });
            await mapView.AddCustomMarkerAsync(marker);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ShowCreateCustomMarkerDialog(int mapId, int coordX, int coordY, int x, int y)
    {
        if (!hasMarkersPermission)
        {
            Snackbar.Add("You don't have permission to create custom markers", Severity.Warning);
            return;
        }

        var parameters = new DialogParameters
        {
            { "MapId", mapId },
            { "CoordX", coordX },
            { "CoordY", coordY },
            { "X", x },
            { "Y", y },
            { "OnMarkerCreated", EventCallback.Factory.Create<CustomMarkerViewModel?>(this, HandleMarkerCreatedAsync) }
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseButton = true
        };

        await DialogService.ShowAsync<CreateCustomMarkerDialog>(
            "Add Custom Marker",
            parameters,
            options);
    }

    private async Task ShowEditCustomMarkerDialog(CustomMarkerViewModel marker)
    {
        var parameters = new DialogParameters
        {
            { "MarkerId", marker.Id },
            { "InitialTitle", marker.Title },
            { "InitialDescription", marker.Description },
            { "InitialIcon", marker.Icon },
            { "InitialHidden", marker.Hidden },
            { "PlacedAt", marker.PlacedAt },
            { "OnMarkerUpdated", EventCallback.Factory.Create(this, RefreshCustomMarkersAsync) }
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseButton = true
        };

        await DialogService.ShowAsync<EditCustomMarkerDialog>(
            "Edit Custom Marker",
            parameters,
            options);
    }

    private async Task DeleteCustomMarkerAsync(CustomMarkerViewModel marker)
    {
        var confirm = await DialogService.ShowMessageBox(
            "Delete Custom Marker",
            $"Are you sure you want to delete the marker '{marker.Title}'?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirm == true)
        {
            try
            {
                var httpClient = HttpClientFactory.CreateClient("API");
                var response = await httpClient.DeleteAsync($"/map/api/v1/custom-markers/{marker.Id}");

                if (response.IsSuccessStatusCode)
                {
                    Snackbar.Add($"Marker '{marker.Title}' deleted", Severity.Success);
                    await RefreshCustomMarkersAsync();
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Snackbar.Add("You don't have permission to delete this marker", Severity.Error);
                }
                else
                {
                    Snackbar.Add("Failed to delete marker", Severity.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting custom marker");
                Snackbar.Add("Error deleting marker", Severity.Error);
            }
        }
    }

    private async Task ShowSetTimerForMarkerDialog(MarkerModel marker)
    {
        // Check if this marker already has an active timer
        var existingTimer = allTimers.FirstOrDefault(t =>
            t.Type == "Marker" &&
            t.MarkerId == marker.Id &&
            !t.IsCompleted);

        DialogParameters parameters;
        string dialogTitle;
        string successMessage;

        if (existingTimer != null)
        {
            // Edit mode - pass existing timer to dialog
            parameters = new DialogParameters
            {
                { "Timer", existingTimer }
            };
            dialogTitle = "Edit Timer";
            successMessage = "Timer updated successfully";
        }
        else
        {
            // Create mode - preset marker association
            parameters = new DialogParameters
            {
                { "PresetType", "Marker" },
                { "PresetMarkerId", marker.Id },
                { "PresetTitle", $"Timer for {marker.Name}" }
            };
            dialogTitle = "Create Timer for Marker";
            successMessage = "Timer created successfully";
        }

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
            CloseButton = true
        };

        var dialog = await DialogService.ShowAsync<CreateTimerDialog>(
            dialogTitle,
            parameters,
            options);

        var result = await dialog.Result;
        if (!result.Canceled)
        {
            await LoadTimersAsync();

            // Re-enrich the marker with new timer data and update its visual representation
            var updatedMarker = MarkerState.AllMarkers.FirstOrDefault(m => m.Id == marker.Id);
            if (updatedMarker != null && mapView != null)
            {
                EnrichMarkersWithTimerData(new[] { updatedMarker });
                await mapView.UpdateMarkerAsync(updatedMarker);
            }

            await InvokeAsync(StateHasChanged);
            Snackbar.Add(successMessage, Severity.Success);
        }
    }

    private async Task ShowSetTimerForCustomMarkerDialog(CustomMarkerViewModel marker)
    {
        // Check if this custom marker already has an active timer
        var existingTimer = allTimers.FirstOrDefault(t =>
            t.Type == "CustomMarker" &&
            t.CustomMarkerId == marker.Id &&
            !t.IsCompleted);

        DialogParameters parameters;
        string dialogTitle;
        string successMessage;

        if (existingTimer != null)
        {
            // Edit mode - pass existing timer to dialog
            parameters = new DialogParameters
            {
                { "Timer", existingTimer }
            };
            dialogTitle = "Edit Timer";
            successMessage = "Timer updated successfully";
        }
        else
        {
            // Create mode - preset marker association
            parameters = new DialogParameters
            {
                { "PresetType", "CustomMarker" },
                { "PresetCustomMarkerId", marker.Id },
                { "PresetTitle", $"Timer for {marker.Title}" }
            };
            dialogTitle = "Create Timer for Marker";
            successMessage = "Timer created successfully";
        }

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
            CloseButton = true
        };

        var dialog = await DialogService.ShowAsync<CreateTimerDialog>(
            dialogTitle,
            parameters,
            options);

        var result = await dialog.Result;
        if (!result.Canceled)
        {
            await LoadTimersAsync();

            // Re-enrich the custom marker with new timer data and update its visual representation
            var updatedCustomMarker = allCustomMarkers.FirstOrDefault(cm => cm.Id == marker.Id);
            if (updatedCustomMarker != null && mapView != null)
            {
                EnrichCustomMarkersWithTimerData(new[] { updatedCustomMarker });
                await mapView.UpdateCustomMarkerAsync(updatedCustomMarker);
            }

            await InvokeAsync(StateHasChanged);
            Snackbar.Add(successMessage, Severity.Success);
        }
    }

    private async Task OnToggleCustomMarkers(bool show)
    {
        LayerVisibility.ShowCustomMarkers = show;
        state.ShowCustomMarkers = show;

        if (mapView != null)
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            await leafletModule.InvokeVoidAsync("toggleCustomMarkers", show);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task TogglePClaim()
    {
        showPClaim = !showPClaim;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "ClaimFloor", showPClaim);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "ClaimOutline", showPClaim);
        await SaveToggleStatesAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleVClaim()
    {
        showVClaim = !showVClaim;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "VillageFloor", showVClaim);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "VillageOutline", showVClaim);
        await SaveToggleStatesAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleProvince()
    {
        showProvince = !showProvince;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province0", showProvince);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province1", showProvince);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province2", showProvince);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province3", showProvince);
        await leafletModule.InvokeVoidAsync("setOverlayTypeEnabled", "Province4", showProvince);
        await SaveToggleStatesAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleThingwallHighlight()
    {
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        var newState = !showThingwallHighlight;
        var success = await leafletModule.InvokeAsync<bool>("setThingwallHighlightEnabled", newState);
        if (success)
        {
            showThingwallHighlight = newState;
            await SaveToggleStatesAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ToggleQuestGiverHighlight()
    {
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        var newState = !showQuestGiverHighlight;
        var success = await leafletModule.InvokeAsync<bool>("setQuestGiverHighlightEnabled", newState);
        if (success)
        {
            showQuestGiverHighlight = newState;
            await SaveToggleStatesAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ToggleMarkerFilterMode()
    {
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        var newState = !showMarkerFilterMode;
        var success = await leafletModule.InvokeAsync<bool>("setMarkerFilterModeEnabled", newState);
        if (success)
        {
            showMarkerFilterMode = newState;
            await SaveToggleStatesAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ToggleRoads()
    {
        showRoads = !showRoads;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("toggleRoads", showRoads);
        await SaveToggleStatesAsync();
        await InvokeAsync(StateHasChanged);
    }

    #region Road SSE Event Handlers

    [JSInvokable]
    public async Task OnRoadCreated(RoadEventDto roadEvent)
    {
        if (roadEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshRoadsAsync();
    }

    [JSInvokable]
    public async Task OnRoadUpdated(RoadEventDto roadEvent)
    {
        if (roadEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshRoadsAsync();
    }

    [JSInvokable]
    public async Task OnRoadDeleted(RoadDeleteEventDto deleteEvent)
    {
        // Remove road from list
        var road = allRoads.FirstOrDefault(r => r.Id == deleteEvent.Id);
        if (road != null)
        {
            allRoads.Remove(road);
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            await leafletModule.InvokeVoidAsync("removeRoad", deleteEvent.Id);
            await InvokeAsync(StateHasChanged);
        }
    }

    #endregion

    #region Overlay SSE Event Handlers

    [JSInvokable]
    public async Task OnOverlayUpdated(OverlayEventDto overlayEvent)
    {
        // Invalidate the overlay cache at the specific coordinate and trigger refetch
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("invalidateOverlayAtCoord",
            overlayEvent.MapId,
            overlayEvent.CoordX,
            overlayEvent.CoordY,
            overlayEvent.OverlayType);
    }

    #endregion

    #region Road Handlers

    private async Task SelectRoad(RoadViewModel road)
    {
        if (road == null) return;
        HideAllContextMenus();
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("selectRoad", road.Id);
    }

    private async Task ShowEditRoadDialog(RoadViewModel road)
    {
        if (road == null) return;
        HideAllContextMenus();

        var parameters = new DialogParameters<EditRoadDialog>
        {
            { x => x.Road, road },
            { x => x.OnRoadUpdated, EventCallback.Factory.Create<RoadViewModel?>(this, async (updated) =>
                {
                    await RefreshRoadsAsync();
                })
            }
        };

        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        await DialogService.ShowAsync<EditRoadDialog>("Edit Road", parameters, options);
    }

    private async Task DeleteRoadAsync(RoadViewModel road)
    {
        if (road == null) return;
        HideAllContextMenus();

        var confirmed = await DialogService.ShowMessageBox(
            "Delete Road",
            $"Are you sure you want to delete the road '{road.Name}'?",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true) return;

        try
        {
            var httpClient = HttpClientFactory.CreateClient("API");
            var response = await httpClient.DeleteAsync($"/map/api/v1/roads/{road.Id}");

            if (response.IsSuccessStatusCode)
            {
                Snackbar.Add($"Road '{road.Name}' deleted", Severity.Success);
                await RefreshRoadsAsync();
            }
            else
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                Snackbar.Add($"Failed to delete road: {errorMsg}", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error deleting road: {ex.Message}", Severity.Error);
        }
    }

    private async Task HandleJumpToRoad(int roadId)
    {
        HideAllContextMenus();
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("jumpToRoad", roadId);
    }

    private async Task HandleEditRoad(int roadId)
    {
        var road = allRoads.FirstOrDefault(r => r.Id == roadId);
        if (road != null)
        {
            await ShowEditRoadDialog(road);
        }
    }

    private async Task HandleDeleteRoad(int roadId)
    {
        var road = allRoads.FirstOrDefault(r => r.Id == roadId);
        if (road != null)
        {
            await DeleteRoadAsync(road);
        }
    }

    private async Task HandleStartDrawRoadFromMenu()
    {
        HideAllContextMenus();
        isDrawingRoad = true;
        drawingPointsCount = 0;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("startDrawingRoad");
        Snackbar.Add("Click to add waypoints. Right-click to finish or cancel.", Severity.Info);
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleFinishRoadFromMenu()
    {
        HideAllContextMenus();
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("finishDrawingRoadFromMenu");
        // Note: JsOnRoadDrawingComplete will be called by JS after finishing
    }

    private async Task HandleCancelRoadFromMenu()
    {
        HideAllContextMenus();
        isDrawingRoad = false;
        drawingPointsCount = 0;
        leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
        await leafletModule.InvokeVoidAsync("cancelDrawingRoad");
        Snackbar.Add("Road drawing cancelled", Severity.Info);
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRoadDrawingComplete((int mapId, List<RoadWaypointDto> waypoints) data)
    {
        var (mapId, waypoints) = data;
        Logger.LogInformation("[Road] HandleRoadDrawingComplete called with mapId={MapId}, waypoints count={Count}",
            mapId, waypoints?.Count ?? 0);

        isDrawingRoad = false;
        drawingPointsCount = 0;
        await InvokeAsync(StateHasChanged);

        if (waypoints == null || waypoints.Count < 2)
        {
            Logger.LogWarning("[Road] Insufficient waypoints: {Count}", waypoints?.Count ?? 0);
            Snackbar.Add("Road requires at least 2 waypoints", Severity.Warning);
            return;
        }

        // Log first waypoint to verify deserialization
        var first = waypoints.First();
        Logger.LogInformation("[Road] First waypoint: CoordX={CoordX}, CoordY={CoordY}, X={X}, Y={Y}",
            first.CoordX, first.CoordY, first.X, first.Y);

        var parameters = new DialogParameters<CreateRoadDialog>
        {
            { x => x.MapId, mapId },
            { x => x.Waypoints, waypoints },
            { x => x.OnRoadCreated, EventCallback.Factory.Create<RoadViewModel?>(this, async (created) =>
                {
                    await RefreshRoadsAsync();
                })
            }
        };

        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        await DialogService.ShowAsync<CreateRoadDialog>("Name Your Road", parameters, options);
    }

    private async Task HandleRoadContextMenu((int roadId, int screenX, int screenY) data)
    {
        var (roadId, screenX, screenY) = data;
        HideAllContextMenus();

        var road = allRoads.FirstOrDefault(r => r.Id == roadId);
        if (road == null) return;

        showRoadContextMenu = true;
        contextRoadId = roadId;
        contextRoadCanEdit = road.CanEdit;
        contextMenuX = screenX;
        contextMenuY = screenY;

        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshRoadsAsync()
    {
        try
        {
            var mapId = MapNavigation.CurrentMapId;
            if (mapId <= 0) return;

            var httpClient = HttpClientFactory.CreateClient("API");
            var response = await httpClient.GetAsync($"/map/api/v1/roads?mapId={mapId}");

            if (response.IsSuccessStatusCode)
            {
                var roadDtos = await response.Content.ReadFromJsonAsync<List<RoadViewDto>>(CamelCaseJsonOptions);
                allRoads = roadDtos?.Select(RoadViewModel.FromDto).ToList() ?? new();

                // Update map display
                leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
                await leafletModule.InvokeVoidAsync("clearAllRoads");
                foreach (var road in allRoads.Where(r => !r.Hidden))
                {
                    await leafletModule.InvokeVoidAsync("addRoad", road);
                }

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing roads");
        }
    }

    #endregion

    #region Navigation Handlers

    private async Task HandleNavigateHereFromMenu()
    {
        HideAllContextMenus();

        if (allRoads.Count == 0)
        {
            Snackbar.Add("No roads available for navigation", Severity.Warning);
            return;
        }

        // Require "my character" to be set for navigation
        if (string.IsNullOrEmpty(myCharacterName))
        {
            Snackbar.Add("Set your character first in the Players panel to use navigation", Severity.Warning);
            return;
        }

        // Find "my character" - must be online
        var myChar = CharacterTracking.AllCharacters.FirstOrDefault(c => c.Name == myCharacterName);
        if (myChar == null)
        {
            Snackbar.Add($"'{myCharacterName}' is not online. Navigation requires your character to be visible.", Severity.Warning);
            return;
        }

        Logger.LogDebug("Using my character '{Name}' as navigation start", myCharacterName);

        // Position.X and Position.Y are absolute coordinates, convert to grid format
        int charCoordX = myChar.Position.X / 100;
        int charCoordY = myChar.Position.Y / 100;
        int charX = myChar.Position.X % 100;
        int charY = myChar.Position.Y % 100;
        var startPoint = (coordX: charCoordX, coordY: charCoordY, x: charX, y: charY);

        // Destination is the right-click location
        var endPoint = new { coordX = mapActionCoordX, coordY = mapActionCoordY, x = mapActionX, y = mapActionY };

        Logger.LogInformation("Navigation: Start ({CoordX},{CoordY},{X},{Y}) -> End ({ECoordX},{ECoordY},{EX},{EY}), Roads count: {RoadsCount}",
            startPoint.coordX, startPoint.coordY, startPoint.x, startPoint.y,
            mapActionCoordX, mapActionCoordY, mapActionX, mapActionY,
            allRoads.Count);

        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");

            // Call JavaScript to find route
            var routeResult = await leafletModule.InvokeAsync<RouteResult>("findRoute",
                new { coordX = startPoint.coordX, coordY = startPoint.coordY, x = startPoint.x, y = startPoint.y },
                endPoint);

            Logger.LogInformation("Navigation result: Success={Success}, Segments={SegmentCount}, Error={Error}",
                routeResult?.Success, routeResult?.Segments?.Count ?? 0, routeResult?.Error ?? "none");

            if (routeResult == null || routeResult.Segments == null || routeResult.Segments.Count == 0)
            {
                if (!string.IsNullOrEmpty(routeResult?.Error))
                {
                    Snackbar.Add(routeResult.Error, Severity.Warning);
                }
                else
                {
                    Snackbar.Add("No route found", Severity.Warning);
                }
                return;
            }

            currentRoute = routeResult;

            // Calculate absolute coordinates for highlighting
            var startAbs = new { x = startPoint.coordX * 100 + startPoint.x, y = startPoint.coordY * 100 + startPoint.y };
            var endAbs = new { x = endPoint.coordX * 100 + endPoint.x, y = endPoint.coordY * 100 + endPoint.y };

            // Highlight the route on the map
            var roadIds = routeResult.Segments.Select(s => s.RoadId).ToArray();
            await leafletModule.InvokeVoidAsync("highlightRoute", roadIds, startAbs, endAbs);

            Snackbar.Add($"Route found: {routeResult.Segments.Count} road(s)", Severity.Success);
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error calculating navigation route");
            Snackbar.Add("Error calculating route", Severity.Error);
        }
    }

    private async Task HandleClearRoute()
    {
        currentRoute = null;

        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            await leafletModule.InvokeVoidAsync("clearRouteHighlight");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing route highlight");
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleJumpToRouteRoad(int roadId)
    {
        try
        {
            leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
            await leafletModule.InvokeVoidAsync("jumpToRoad", roadId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error jumping to road {RoadId}", roadId);
        }
    }

    #endregion

    [JSInvokable]
    public async Task OnCustomMarkerCreated(CustomMarkerEventModel markerEvent)
    {
        if (markerEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshCustomMarkersAsync();
    }

    [JSInvokable]
    public async Task OnCustomMarkerUpdated(CustomMarkerEventModel markerEvent)
    {
        if (markerEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshCustomMarkersAsync();
    }

    [JSInvokable]
    public async Task OnCustomMarkerDeleted(System.Text.Json.JsonElement data)
    {
        var id = data.GetProperty("Id").GetInt32();
        CustomMarkerState.RemoveCustomMarker(id);

        if (mapView != null)
        {
            await mapView.RemoveCustomMarkerAsync(id);
        }

        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnMarkerCreated(MarkerEventModel markerEvent)
    {
        if (markerEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshMarkersAsync();
    }

    [JSInvokable]
    public async Task OnMarkerUpdated(MarkerEventModel markerEvent)
    {
        if (markerEvent.MapId != MapNavigation.CurrentMapId) return;
        await RefreshMarkersAsync();
    }

    [JSInvokable]
    public async Task OnMarkerDeleted(System.Text.Json.JsonElement data)
    {
        // Game markers are deleted by GridId + position, refresh all markers
        await RefreshMarkersAsync();
    }

    private async Task RefreshMarkersAsync()
    {
        try
        {
            var markers = await MapData.GetMarkersAsync();
            MarkerState.SetMarkers(markers);
            await LoadMarkersForCurrentMapAsync();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing markers");
        }
    }

    private async Task HandleCreateCustomMarkerFromMenu()
    {
        showMapActionMenu = false;
        await ShowCreateCustomMarkerDialog(mapActionMapId, mapActionCoordX, mapActionCoordY, mapActionX, mapActionY);
    }

    private async Task HandleCreatePingFromMenu()
    {
        showMapActionMenu = false;
        await JsCreatePing(mapActionMapId, mapActionCoordX, mapActionCoordY, mapActionX, mapActionY);
    }

    private async Task HandleDeleteTileFromMenu()
    {
        showMapActionMenu = false;

        try
        {
            var success = await MapData.WipeTileAsync(mapActionMapId, mapActionCoordX, mapActionCoordY);
            if (success)
            {
                Snackbar.Add($"Tile ({mapActionCoordX}, {mapActionCoordY}) deleted", Severity.Success);
                // Force map refresh
                if (mapView != null)
                {
                    await mapView.RefreshTilesAsync();
                }
            }
            else
            {
                Snackbar.Add("Failed to delete tile", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete tile at ({X}, {Y})", mapActionCoordX, mapActionCoordY);
            Snackbar.Add("Failed to delete tile", Severity.Error);
        }
    }

    private async Task HandleShowTileInfo()
    {
        showMapActionMenu = false;
        tileInfoLoading = true;
        showTileInfoDialog = true;
        tileInfoResult = null;
        StateHasChanged();

        try
        {
            var url = $"/api/tile-info?mapId={mapActionMapId}&x={mapActionCoordX}&y={mapActionCoordY}";
            Logger.LogInformation("Fetching tile info from: {Url}", url);

            // Use JS fetch to call the Web service endpoint directly
            tileInfoResult = await JS.InvokeAsync<TileInfoResult?>("fetchTileInfo", url);
            Logger.LogInformation("Fetched tile info: GridId={GridId}, MapId={MapId}",
                tileInfoResult?.GridId, tileInfoResult?.MapId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to fetch tile info for ({X}, {Y})", mapActionCoordX, mapActionCoordY);
        }
        finally
        {
            tileInfoLoading = false;
            StateHasChanged();
        }
    }

    private async Task CopyToClipboard(string text)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
        Snackbar.Add("Copied to clipboard", Severity.Info);
    }

    private record TileInfoResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("x")] int X,
        [property: System.Text.Json.Serialization.JsonPropertyName("y")] int Y,
        [property: System.Text.Json.Serialization.JsonPropertyName("gridId")] string GridId,
        [property: System.Text.Json.Serialization.JsonPropertyName("mapId")] int MapId,
        [property: System.Text.Json.Serialization.JsonPropertyName("nextUpdate")] DateTime NextUpdate);

    /// <summary>
    /// Creates a ping at the specified location (called from JavaScript Alt+M shortcut or context menu)
    /// </summary>
    [JSInvokable]
    public async Task JsCreatePing(int mapId, int coordX, int coordY, int x, int y)
    {
        try
        {
            Logger.LogWarning("JsCreatePing called: MapId={MapId}, Grid=({CoordX},{CoordY}), Local=({X},{Y})",
                mapId, coordX, coordY, x, y);

            var client = HttpClientFactory.CreateClient("API");

            var pingDto = new
            {
                mapId,
                coordX,
                coordY,
                x,
                y
            };

            var response = await client.PostAsJsonAsync("/map/api/v1/pings", pingDto, CamelCaseJsonOptions);

            if (response.IsSuccessStatusCode)
            {
                Logger.LogInformation("Ping created successfully at Map={MapId}, Grid=({CoordX},{CoordY}), Local=({X},{Y})",
                    mapId, coordX, coordY, x, y);
                Snackbar.Add($"Ping created at ({coordX},{coordY})", Severity.Success);
                // Ping will be added via SSE event
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Snackbar.Add("You have reached the maximum of 5 active pings. Please wait for existing pings to expire.", Severity.Warning);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                Logger.LogWarning("Failed to create ping: Status={StatusCode}, Response={ResponseBody}, Request=MapId={MapId} Grid=({CoordX},{CoordY}) Local=({X},{Y})",
                    response.StatusCode, responseBody, mapId, coordX, coordY, x, y);
                Snackbar.Add($"Failed to create ping: {response.StatusCode}", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating ping at MapId={MapId}, Grid=({CoordX},{CoordY}), Local=({X},{Y})",
                mapId, coordX, coordY, x, y);
            Snackbar.Add("Error creating ping: " + ex.Message, Severity.Error);
        }
    }

    /// <summary>
    /// Handles ping created events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnPingCreated(PingEventModel pingData)
    {
        try
        {
            Console.WriteLine($"[Map.razor.cs] OnPingCreated called - Id:{pingData.Id}, MapId:{pingData.MapId}, CurrentMapId:{MapNavigation.CurrentMapId}");

            // Only process pings for the currently viewed map
            if (pingData.MapId != MapNavigation.CurrentMapId)
            {
                Console.WriteLine($"[Map.razor.cs] Ping is for different map (ping:{pingData.MapId}, current:{MapNavigation.CurrentMapId}), ignoring");
                return;
            }

            Console.WriteLine($"[Map.razor.cs] Ping is for current map, coords:({pingData.CoordX},{pingData.CoordY},{pingData.X},{pingData.Y})");

            // Forward to JavaScript ping manager
            if (mapView != null)
            {
                Console.WriteLine("[Map.razor.cs] mapView is not null, getting leaflet module");
                leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
                Console.WriteLine("[Map.razor.cs] Calling onPingCreated in JS");
                await leafletModule.InvokeVoidAsync("onPingCreated", pingData);
                Console.WriteLine("[Map.razor.cs] onPingCreated completed");

                // Show the "Go to Ping" button
                hasActivePing = true;
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                Console.WriteLine("[Map.razor.cs] ERROR: mapView is null!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Map.razor.cs] ERROR in OnPingCreated: {ex.Message}");
            Logger.LogError(ex, "Error handling ping created event");
        }
    }

    /// <summary>
    /// Handles ping deleted events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnPingDeleted(PingDeleteEventModel deleteData)
    {
        try
        {
            Console.WriteLine($"[Map.razor.cs] OnPingDeleted called - Id:{deleteData.Id}");

            // Forward to JavaScript ping manager
            if (mapView != null)
            {
                leafletModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./js/leaflet-interop.js{JsVersion}");
                await leafletModule.InvokeVoidAsync("onPingDeleted", deleteData.Id);

                // Check if there are still active pings
                var activePingCount = await leafletModule.InvokeAsync<int>("getActivePingCount");
                hasActivePing = activePingCount > 0;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling ping deleted event");
        }
    }

    #endregion

    #region Overlay Helpers

    /// <summary>
    /// Handles overlay data requests from JavaScript overlay layer via MapView callback.
    /// Fetches data from API via MapDataService and returns it to JS.
    /// </summary>
    private async Task HandleRequestOverlays((int mapId, string coords) request)
    {
        try
        {
            Logger.LogDebug("HandleRequestOverlays: Received request for map {MapId}, coords: {Coords}", request.mapId, request.coords);

            // Fetch overlay data from API via MapDataService (server-to-server)
            var overlays = await MapData.GetOverlaysAsync(request.mapId, request.coords);

            Logger.LogDebug("HandleRequestOverlays: Got {Count} overlays, sending to JS", overlays.Count);

            // Send overlay data back to JavaScript via MapView
            if (mapView != null)
            {
                await mapView.SetOverlayDataAsync(request.mapId, overlays);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching overlay data for map {MapId}", request.mapId);
        }
    }

    #endregion

    #region Timer Helpers

    /// <summary>
    /// Handles timer created events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnTimerCreated(TimerDto timer)
    {
        try
        {
            Logger.LogInformation("OnTimerCreated: Received timer {TimerId} ({Title})", timer.Id, timer.Title);

            // Create new list reference to force Blazor change detection
            var newList = new List<TimerDto>(allTimers);

            // Add if not exists
            if (!newList.Any(t => t.Id == timer.Id))
            {
                newList.Add(timer);
                allTimers = newList; // Update reference
                Logger.LogInformation("OnTimerCreated: Added timer {TimerId} to list", timer.Id);
            }
            else
            {
                Logger.LogWarning("OnTimerCreated: Timer {TimerId} already exists, ignoring", timer.Id);
            }

            // Refresh markers/custom markers
            await RefreshTimerVisualsAsync();

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling timer created event");
        }
    }

    /// <summary>
    /// Handles timer updated events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnTimerUpdated(TimerDto timer)
    {
        try
        {
            Logger.LogInformation("OnTimerUpdated: Received timer {TimerId} ({Title})", timer.Id, timer.Title);

            // Create new list reference
            var newList = new List<TimerDto>(allTimers);
            var existing = newList.FirstOrDefault(t => t.Id == timer.Id);
            
            if (existing != null)
            {
                newList.Remove(existing);
                newList.Add(timer);
                allTimers = newList; // Update reference
                Logger.LogInformation("OnTimerUpdated: Updated timer {TimerId} in list", timer.Id);
            }
            else
            {
                newList.Add(timer);
                allTimers = newList; // Update reference
                Logger.LogInformation("OnTimerUpdated: Added new timer {TimerId} (was missing)", timer.Id);
            }
            
            // Refresh markers/custom markers
            await RefreshTimerVisualsAsync();
            
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling timer updated event");
        }
    }

    /// <summary>
    /// Handles timer completed events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnTimerCompleted(TimerDto timer)
    {
        try
        {
            Logger.LogInformation("OnTimerCompleted: Received timer {TimerId} ({Title})", timer.Id, timer.Title);

            // Create new list reference
            var newList = new List<TimerDto>(allTimers);
            var existing = newList.FirstOrDefault(t => t.Id == timer.Id);
            
            if (existing != null)
            {
                // We need to replace the object to ensure property changes are detected if binding directly
                newList.Remove(existing);
                
                // Update properties
                existing.IsCompleted = true;
                existing.CompletedAt = timer.CompletedAt;
                
                newList.Add(existing);
                allTimers = newList; // Update reference
                
                Logger.LogInformation("OnTimerCompleted: Marked timer {TimerId} as completed", timer.Id);
            }
            
            // Refresh markers/custom markers (timer should disappear from map)
            await RefreshTimerVisualsAsync();
            
            await InvokeAsync(StateHasChanged);
            
            // Show notification
            Snackbar.Add($"Timer '{timer.Title}' completed!", Severity.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling timer completed event");
        }
    }

    /// <summary>
    /// Handles timer deleted events from SSE
    /// </summary>
    [JSInvokable]
    public async Task OnTimerDeleted(int timerId)
    {
        try
        {
            Logger.LogInformation("OnTimerDeleted: Received timer {TimerId}", timerId);

            // Create new list reference
            var newList = new List<TimerDto>(allTimers);
            var existing = newList.FirstOrDefault(t => t.Id == timerId);

            if (existing != null)
            {
                newList.Remove(existing);
                allTimers = newList; // Update reference
                Logger.LogInformation("OnTimerDeleted: Removed timer {TimerId} from list", timerId);
            }

            // Refresh markers/custom markers (remove timer badge)
            await RefreshTimerVisualsAsync();

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling timer deleted event");
        }
    }

    private async Task RefreshTimerVisualsAsync()
    {
        // Update resource markers
        if (LayerVisibility.ShowMarkers)
        {
            var markersToUpdate = MarkerState.AllMarkers.Where(m => 
                allTimers.Any(t => t.Type == "Marker" && t.MarkerId == m.Id)).ToList();
            
            if (markersToUpdate.Any())
            {
                EnrichMarkersWithTimerData(markersToUpdate);
                foreach (var marker in markersToUpdate)
                {
                    await mapView.UpdateMarkerAsync(marker);
                }
            }
        }

        // Update custom markers
        if (LayerVisibility.ShowCustomMarkers)
        {
            var customMarkersToUpdate = allCustomMarkers.Where(cm => 
                allTimers.Any(t => t.Type == "CustomMarker" && t.CustomMarkerId == cm.Id)).ToList();
                
            if (customMarkersToUpdate.Any())
            {
                EnrichCustomMarkersWithTimerData(customMarkersToUpdate);
                foreach (var cm in customMarkersToUpdate)
                {
                    await mapView.UpdateCustomMarkerAsync(cm);
                }
            }
        }
    }

    /// <summary>
    /// Enriches markers with timer countdown text for display on map
    /// </summary>
    private void EnrichMarkersWithTimerData(IEnumerable<MarkerModel> markers)
    {
        var now = DateTime.UtcNow;

        foreach (var marker in markers)
        {
            // Find timer for this marker
            var timer = allTimers.FirstOrDefault(t =>
                t.Type == "Marker" &&
                t.MarkerId == marker.Id &&
                !t.IsCompleted &&
                t.ReadyAt > now);

            if (timer != null)
            {
                var remaining = timer.ReadyAt - now;
                marker.TimerText = FormatTimerCountdown(remaining);
            }
            else
            {
                marker.TimerText = null;
            }
        }
    }

    /// <summary>
    /// Enriches custom markers with timer countdown text for display on map
    /// </summary>
    private void EnrichCustomMarkersWithTimerData(IEnumerable<CustomMarkerViewModel> customMarkers)
    {
        var now = DateTime.UtcNow;

        foreach (var customMarker in customMarkers)
        {
            // Find timer for this custom marker
            var timer = allTimers.FirstOrDefault(t =>
                t.Type == "CustomMarker" &&
                t.CustomMarkerId == customMarker.Id &&
                !t.IsCompleted &&
                t.ReadyAt > now);

            if (timer != null)
            {
                var remaining = timer.ReadyAt - now;
                customMarker.TimerText = FormatTimerCountdown(remaining);
            }
            else
            {
                customMarker.TimerText = null;
            }
        }
    }

    /// <summary>
    /// Formats a timespan into a compact countdown string (e.g., "2h 15m", "45m", "30s")
    /// </summary>
    private string FormatTimerCountdown(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0)
            return "Ready!";

        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";

        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";

        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}m";

        return $"{(int)remaining.TotalSeconds}s";
    }

    #endregion

    #region IBrowserViewportObserver Implementation

    Guid IBrowserViewportObserver.Id { get; } = Guid.NewGuid();

    ResizeOptions IBrowserViewportObserver.ResizeOptions { get; } = new()
    {
        ReportRate = 250,
        NotifyOnBreakpointOnly = true
    };

    Task IBrowserViewportObserver.NotifyBrowserViewportChangeAsync(BrowserViewportEventArgs browserViewportEventArgs)
    {
        var breakpoint = browserViewportEventArgs.Breakpoint;
        var shouldBeOpen = breakpoint >= Breakpoint.Lg;

        if (isSidebarOpen != shouldBeOpen)
        {
            isSidebarOpen = shouldBeOpen;
            Logger.LogInformation("Viewport breakpoint changed to {Breakpoint}, sidebar now {State}",
                breakpoint, isSidebarOpen ? "open" : "closed");
            return InvokeAsync(StateHasChanged);
        }

        return Task.CompletedTask;
    }

    #endregion
}
