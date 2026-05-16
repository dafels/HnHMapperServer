using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Web.Models;
using HnHMapperServer.Web.Services.Map;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text.Json;

namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// Native Blazor map renderer. Drop-in replacement for <see cref="MapView"/> that
/// does not use Leaflet. Owns its pan/zoom math and tile placement; relies on a
/// small generic JS shim (<c>blazor-map-shim.js</c>) for pointer/wheel batching
/// and CSS-variable nudges only.
///
/// Performance design: the pan layer is shifted by a CSS transform driven by the
/// shim. Tiles and entities inside the pan layer use STABLE pan-layer coordinates
/// (constant per tile), so a pan that doesn't cross a tile boundary causes zero
/// DOM diff — the shim moves everything via one CSS variable change. The C#
/// renderer only fires when the visible tile range changes (every ~100 px of pan).
/// </summary>
public partial class BlazorMapView : ComponentBase, IMapView, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    #region Parameters (mirror MapView.razor)

    [Parameter] public MapState State { get; set; } = new();
    [Parameter] public EventCallback<(int x, int y, int z)> OnMapDragged { get; set; }
    [Parameter] public EventCallback<(int x, int y, int z)> OnMapZoomed { get; set; }
    [Parameter] public EventCallback<(int gridX, int gridY, int screenX, int screenY)> OnContextMenu { get; set; }
    [Parameter] public EventCallback<int> OnMarkerClicked { get; set; }
    [Parameter] public EventCallback<(int markerId, int screenX, int screenY)> OnMarkerContextMenu { get; set; }
    [Parameter] public EventCallback<(int customMarkerId, int screenX, int screenY)> OnCustomMarkerContextMenu { get; set; }
    [Parameter] public EventCallback<(int mapId, int coordX, int coordY, int x, int y, int screenX, int screenY)> OnMapRightClick { get; set; }
    [Parameter] public EventCallback<bool> OnMapInitialized { get; set; }
    [Parameter] public EventCallback<int> OnMapChanged { get; set; }
    [Parameter] public EventCallback<(int mapId, string coords)> OnRequestOverlays { get; set; }
    [Parameter] public EventCallback<(int mapId, List<RoadWaypointDto> waypoints)> OnRoadDrawingComplete { get; set; }
    [Parameter] public EventCallback<(int roadId, int screenX, int screenY)> OnRoadContextMenu { get; set; }

    [Parameter] public IReadOnlyList<RoadViewModel>? Roads { get; set; }
    [Parameter] public bool ShowRoads { get; set; } = true;

    /// <summary>
    /// When true, only markers that are "highlighted" (thingwalls when ShowThingwallHighlight,
    /// quest givers when ShowQuestGiverHighlight) are visible. Everything else is hidden.
    /// </summary>
    [Parameter] public bool ShowMarkerFilterMode { get; set; }
    [Parameter] public bool ShowThingwallHighlight { get; set; }
    [Parameter] public bool ShowQuestGiverHighlight { get; set; }

    #endregion

    #region State

    private ElementReference viewportEl;
    private IJSObjectReference? shim;
    private DotNetObjectReference<BlazorMapView>? dotnetRef;
    private bool initialized;
    private bool disposed;
    private bool dirty = true;
    private bool initialPanSet;

    private MapViewport viewport = new(0, 0, 1, 0, 0);
    private int currentMapId;
    private (int MinX, int MinY, int MaxX, int MaxY) lastEmittedTileRange;
    private double viewportRectLeft;
    private double viewportRectTop;

    // Zoom cross-fade: snapshot of tiles at the previous zoom level. Rendered
    // underneath the new tile layer, scaled to compensate for the zoom change,
    // and faded out via CSS animation. Cleared after the transition timer fires.
    private List<TileGrid.VisibleTile>? oldZoomLayer;
    private int oldZoomScale = 1;
    private int oldZoomLayerVersion;
    private System.Threading.Timer? zoomTransitionTimer;
    // Slightly longer than the CSS animation (1200 ms) so the layer doesn't get
    // ripped out mid-fade. Per-tile load fade-in covers the rest of the gap.
    private const int ZoomTransitionMs = 1500;

    private TileGrid? _tileGrid;
    private TileGrid tileGrid => _tileGrid ??= new TileGrid(Navigation);

    private readonly Dictionary<int, CharacterModel> characters = new();
    private readonly Dictionary<int, MarkerModel> markers = new();
    private readonly HashSet<string> hiddenMarkerTypes = new();

    public bool IsInitialized => initialized;

    /// <summary>
    /// How many tiles beyond the visible viewport to render. Acts as a prefetch ring:
    /// the browser fetches these eagerly so panning into them doesn't show a loading
    /// flash. They're clipped by the viewport's overflow:hidden.
    /// </summary>
    private const int TileRenderMargin = 3;

    #endregion

    protected override void OnInitialized()
    {
        viewport = viewport.WithCenter(State.CenterX, State.CenterY).WithZoom(MapMath.ClampZoom(State.Zoom));
        currentMapId = State.CurrentMapId;

        LayerVisibility.OnChange += HandleServiceChanged;
        CustomMarkerState.OnChange += HandleServiceChanged;
    }

    protected override void OnParametersSet()
    {
        dirty = true;
    }

    private void HandleServiceChanged()
    {
        InvokeAsync(Invalidate);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                shim = await JS.InvokeAsync<IJSObjectReference>("import", $"./js/blazor-map-shim.js?v={BuildInfo.Get("web").Commit}");
                dotnetRef = DotNetObjectReference.Create(this);
                await shim.InvokeVoidAsync("init", viewportEl, dotnetRef);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load blazor-map-shim.js");
                return;
            }
        }
    }

    protected override bool ShouldRender()
    {
        if (!dirty) return false;
        dirty = false;
        lastEmittedTileRange = MapMath.VisibleTileRange(viewport, TileRenderMargin);
        return true;
    }

    private void Invalidate()
    {
        dirty = true;
        StateHasChanged();
    }

    /// <summary>True if a pan-layer point is within the visible viewport (plus margin).</summary>
    private bool IsInVisibleWorldRange(int px, int py, int marginPx)
    {
        var halfW = viewport.Width / 2.0;
        var halfH = viewport.Height / 2.0;
        if (px < viewport.CamPxX - halfW - marginPx) return false;
        if (px > viewport.CamPxX + halfW + marginPx) return false;
        if (py < viewport.CamPxY - halfH - marginPx) return false;
        if (py > viewport.CamPxY + halfH + marginPx) return false;
        return true;
    }

    /// <summary>Push the current camera position to the shim as an absolute --pan-x value.</summary>
    private ValueTask SyncShimPanAsync()
    {
        if (shim == null) return ValueTask.CompletedTask;
        var (px, py) = MapMath.PanLayerOffset(viewport);
        return shim.InvokeVoidAsync("setPan", viewportEl, px, py);
    }

    /// <summary>
    /// Capture the current tile set as the "old zoom layer" so the renderer can
    /// keep displaying it (scaled, fading) while new-zoom tiles load. Call BEFORE
    /// updating viewport.Zoom.
    /// </summary>
    private void StartZoomTransition()
    {
        if (viewport.Width == 0 || currentMapId == 0) return;
        oldZoomLayer = tileGrid.GetVisibleTiles(viewport, currentMapId, TileRenderMargin).ToList();
        oldZoomScale = viewport.Scale;
        oldZoomLayerVersion++;
        zoomTransitionTimer?.Dispose();
        zoomTransitionTimer = new System.Threading.Timer(_ =>
        {
            _ = InvokeAsync(() =>
            {
                oldZoomLayer = null;
                Invalidate();
            });
        }, null, ZoomTransitionMs, System.Threading.Timeout.Infinite);
    }

    #region Shim callbacks (JSInvokable)

    [JSInvokable]
    public async Task OnViewportResize(int width, int height, int rectLeft, int rectTop)
    {
        viewport = viewport.WithSize(width, height);
        viewportRectLeft = rectLeft;
        viewportRectTop = rectTop;
        if (!initialPanSet)
        {
            await SyncShimPanAsync();
            initialPanSet = true;
            if (!initialized)
            {
                initialized = true;
                Logger.LogInformation("BlazorMapView ready ({W}x{H})", width, height);
                _ = OnMapInitialized.InvokeAsync(true);
            }
        }
        Invalidate();
    }

    /// <summary>
    /// Shim posts pan deltas on every rAF tick. We update CenterX/CenterY immediately
    /// but only trigger a re-render when the visible tile range actually changes —
    /// otherwise the shim's CSS transform shifts the whole layer for free and no DOM
    /// diff is needed.
    /// </summary>
    [JSInvokable]
    public Task OnPanDelta(int dx, int dy)
    {
        var dCenterX = -dx * viewport.Scale / 100.0;
        var dCenterY = -dy * viewport.Scale / 100.0;
        viewport = viewport.WithCenter(viewport.CenterX + dCenterX, viewport.CenterY + dCenterY);

        var newRange = MapMath.VisibleTileRange(viewport, TileRenderMargin);
        if (newRange != lastEmittedTileRange)
        {
            Invalidate();
        }
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task OnPanEnd()
    {
        await OnMapDragged.InvokeAsync(((int)Math.Round(viewport.CenterX), (int)Math.Round(viewport.CenterY), viewport.Zoom));
    }

    [JSInvokable]
    public async Task OnZoom(int sign, double anchorX, double anchorY)
    {
        var newZoom = MapMath.ClampZoom(viewport.Zoom + sign);
        if (newZoom == viewport.Zoom) return;
        StartZoomTransition();
        var (cx, cy) = MapMath.ZoomAnchorCenter(viewport, newZoom, anchorX, anchorY);
        viewport = viewport.WithZoom(newZoom).WithCenter(cx, cy);
        await SyncShimPanAsync();
        Invalidate();
        await OnMapZoomed.InvokeAsync(((int)Math.Round(viewport.CenterX), (int)Math.Round(viewport.CenterY), viewport.Zoom));
    }

    [JSInvokable]
    public Task OnZoomTransitionEnd()
    {
        // Reserved for v1.5 cross-fade animation; no-op for now.
        return Task.CompletedTask;
    }

    #endregion

    #region Right-click + marker click

    private async Task HandleContextMenu(MouseEventArgs e)
    {
        // ClientX/Y is page-relative; subtract the viewport's page offset (refreshed by
        // the shim on resize + scroll) to get reliable viewport-local CSS px. OffsetX/Y
        // is unreliable here because pan-layer children can be the event target and the
        // pan layer is CSS-transformed.
        var sx = e.ClientX - viewportRectLeft;
        var sy = e.ClientY - viewportRectTop;
        var (coordX, coordY, localX, localY) = MapMath.ScreenToWorld(viewport, sx, sy);

        if (e.CtrlKey)
        {
            await OnContextMenu.InvokeAsync((coordX, coordY, (int)e.ClientX, (int)e.ClientY));
        }
        else
        {
            await OnMapRightClick.InvokeAsync((currentMapId, coordX, coordY, localX, localY, (int)e.ClientX, (int)e.ClientY));
        }
    }

    private async Task HandleMarkerClick(int markerId)
    {
        await OnMarkerClicked.InvokeAsync(markerId);
    }

    private async Task HandleMarkerContextMenu(int markerId, MouseEventArgs e)
    {
        await OnMarkerContextMenu.InvokeAsync((markerId, (int)e.ClientX, (int)e.ClientY));
    }

    private async Task HandleCustomMarkerContextMenu(int markerId, MouseEventArgs e)
    {
        await OnCustomMarkerContextMenu.InvokeAsync((markerId, (int)e.ClientX, (int)e.ClientY));
    }

    private async Task HandleRoadContextMenu(int roadId, MouseEventArgs e)
    {
        await OnRoadContextMenu.InvokeAsync((roadId, (int)e.ClientX, (int)e.ClientY));
    }

    private static string ResolveCustomMarkerIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "/gfx/terobjs/mm/custom.png";
        var t = icon.Trim();
        return t.StartsWith('/') ? t : "/" + t;
    }

    private static readonly string[] RoadColorPalette = new[]
    {
        "#FF6B6B", "#4ECDC4", "#FFE66D", "#95E1D3", "#F38181", "#AA96DA",
        "#FCBAD3", "#A8D8EA", "#FF9F43", "#5CD85A", "#DDA0DD", "#87CEEB",
        "#F0E68C", "#98D8C8", "#C9B1FF", "#FFB6C1"
    };

    private static string RoadColor(int roadId)
        => RoadColorPalette[((roadId % RoadColorPalette.Length) + RoadColorPalette.Length) % RoadColorPalette.Length];

    #endregion

    #region Marker clustering

    private readonly record struct MarkerDisplayItem(int Sx, int Sy, bool IsCluster, int Count, MarkerModel? Marker, string SortKey);

    /// <summary>
    /// Build the marker layer's visible items, clustering at lower zooms.
    /// Positions are in pan-layer coordinates (constant per render under pan).
    /// </summary>
    private List<MarkerDisplayItem> ComputeMarkerDisplay()
    {
        var items = new List<MarkerDisplayItem>();
        var cluster = LayerVisibility.ShowClustering && viewport.Zoom > 1;
        const int radius = 60;
        var bucketSize = radius;

        bool PassesVisibility(MarkerModel m)
        {
            if (m.Map != currentMapId || m.Hidden) return false;
            // Marker panel hides groups by full image path (e.g. "gfx/invobjs/small/bush"),
            // so we match m.Image, not the derived m.Type short name.
            if (hiddenMarkerTypes.Contains(m.Image)) return false;
            if (ShowMarkerFilterMode)
            {
                var isHighlighted =
                    (m.Type == "thingwall" && ShowThingwallHighlight) ||
                    (m.Type == "questgiver" && ShowQuestGiverHighlight);
                if (!isHighlighted) return false;
            }
            return true;
        }

        if (!cluster)
        {
            foreach (var m in markers.Values)
            {
                if (!PassesVisibility(m)) continue;
                var (px, py) = MapMath.WorldPxToPanLayer(viewport, m.Position.X, m.Position.Y);
                if (!IsInVisibleWorldRange(px, py, 32)) continue;
                items.Add(new MarkerDisplayItem(px, py, false, 1, m, m.Id.ToString()));
            }
            return items;
        }

        // Spatial hash uses pan-layer coords (which translate 1:1 to on-screen at fixed zoom).
        var buckets = new Dictionary<(int, int), List<(int px, int py, MarkerModel m)>>();
        foreach (var m in markers.Values)
        {
            if (!PassesVisibility(m)) continue;
            var (px, py) = MapMath.WorldPxToPanLayer(viewport, m.Position.X, m.Position.Y);
            if (!IsInVisibleWorldRange(px, py, 32)) continue;

            if (m.Type == "thingwall" || m.Type == "questgiver" || m.Type == "custom")
            {
                items.Add(new MarkerDisplayItem(px, py, false, 1, m, m.Id.ToString()));
                continue;
            }

            var key = (px / bucketSize, py / bucketSize);
            if (!buckets.TryGetValue(key, out var list)) { list = new(); buckets[key] = list; }
            list.Add((px, py, m));
        }

        foreach (var (key, list) in buckets)
        {
            if (list.Count == 1)
            {
                var only = list[0];
                items.Add(new MarkerDisplayItem(only.px, only.py, false, 1, only.m, only.m.Id.ToString()));
            }
            else
            {
                var cx = list.Sum(p => p.px) / list.Count;
                var cy = list.Sum(p => p.py) / list.Count;
                items.Add(new MarkerDisplayItem(cx, cy, true, list.Count, null, $"b{key.Item1}_{key.Item2}"));
            }
        }
        return items;
    }

    #endregion

    #region IMapView — navigation

    public Task ChangeMapAsync(int mapId)
    {
        if (currentMapId == mapId) return Task.CompletedTask;
        currentMapId = mapId;
        State.CurrentMapId = mapId;
        Invalidate();
        return OnMapChanged.InvokeAsync(mapId);
    }

    public Task SetOverlayMapAsync(int? mapId, double offsetX = 0, double offsetY = 0)
    {
        State.OverlayMapId = mapId;
        State.OverlayOffsetX = offsetX;
        State.OverlayOffsetY = offsetY;
        return Task.CompletedTask;
    }

    public Task SetOverlayOffsetAsync(double offsetX, double offsetY)
    {
        State.OverlayOffsetX = offsetX;
        State.OverlayOffsetY = offsetY;
        return Task.CompletedTask;
    }

    public async Task SetViewAsync(int gridX, int gridY, int zoom)
    {
        var clamped = MapMath.ClampZoom(zoom);
        if (clamped != viewport.Zoom) StartZoomTransition();
        viewport = viewport.WithCenter(gridX, gridY).WithZoom(clamped);
        State.CenterX = gridX;
        State.CenterY = gridY;
        State.Zoom = viewport.Zoom;
        await SyncShimPanAsync();
        Invalidate();
    }

    public async Task ZoomOutAsync()
    {
        var newZoom = MapMath.ClampZoom(viewport.Zoom + 1);
        if (newZoom == viewport.Zoom) return;
        StartZoomTransition();
        viewport = viewport.WithZoom(newZoom);
        await SyncShimPanAsync();
        Invalidate();
        await OnMapZoomed.InvokeAsync(((int)Math.Round(viewport.CenterX), (int)Math.Round(viewport.CenterY), viewport.Zoom));
    }

    public Task ToggleGridCoordinatesAsync(bool visible)
    {
        State.ShowGridCoordinates = visible;
        Invalidate();
        return Task.CompletedTask;
    }

    public Task RefreshTilesAsync()
    {
        Invalidate();
        return Task.CompletedTask;
    }

    #endregion

    #region IMapView — characters

    public Task AddCharacterAsync(CharacterModel character) { characters[character.Id] = character; Invalidate(); return Task.CompletedTask; }
    public Task UpdateCharacterAsync(CharacterModel character) { characters[character.Id] = character; Invalidate(); return Task.CompletedTask; }
    public Task RemoveCharacterAsync(int characterId) { if (characters.Remove(characterId)) Invalidate(); return Task.CompletedTask; }
    public Task ClearAllCharactersAsync() { if (characters.Count == 0) return Task.CompletedTask; characters.Clear(); Invalidate(); return Task.CompletedTask; }

    public Task SetCharactersSnapshotAsync(object charactersData)
    {
        var list = CoerceList<CharacterModel>(charactersData);
        if (list == null) return Task.CompletedTask;
        characters.Clear();
        foreach (var c in list) characters[c.Id] = c;
        Invalidate();
        return Task.CompletedTask;
    }

    public Task ApplyCharacterDeltaAsync(object delta)
    {
        var d = CoerceObject<CharacterDeltaModel>(delta);
        if (d == null) return Task.CompletedTask;
        if (d.Updates != null) foreach (var c in d.Updates) characters[c.Id] = c;
        if (d.Deletions != null) foreach (var id in d.Deletions) characters.Remove(id);
        Invalidate();
        return Task.CompletedTask;
    }

    #endregion

    #region IMapView — markers / custom markers

    public Task AddMarkerAsync(MarkerModel marker) { markers[marker.Id] = marker; Invalidate(); return Task.CompletedTask; }
    public Task AddMarkersAsync(IEnumerable<MarkerModel> markersToAdd) { foreach (var m in markersToAdd) markers[m.Id] = m; Invalidate(); return Task.CompletedTask; }
    public Task UpdateMarkerAsync(MarkerModel marker) { markers[marker.Id] = marker; Invalidate(); return Task.CompletedTask; }
    public Task RemoveMarkerAsync(int markerId) { if (markers.Remove(markerId)) Invalidate(); return Task.CompletedTask; }
    public Task ClearAllMarkersAsync() { if (markers.Count == 0) return Task.CompletedTask; markers.Clear(); Invalidate(); return Task.CompletedTask; }

    public Task SetHiddenMarkerTypesAsync(IEnumerable<string> hiddenTypes)
    {
        hiddenMarkerTypes.Clear();
        foreach (var t in hiddenTypes) hiddenMarkerTypes.Add(t);
        Invalidate();
        return Task.CompletedTask;
    }

    public Task AddCustomMarkerAsync(CustomMarkerViewModel marker) { Invalidate(); return Task.CompletedTask; }
    public Task UpdateCustomMarkerAsync(CustomMarkerViewModel marker) { Invalidate(); return Task.CompletedTask; }
    public Task RemoveCustomMarkerAsync(int markerId) { Invalidate(); return Task.CompletedTask; }
    public Task ClearAllCustomMarkersAsync() { Invalidate(); return Task.CompletedTask; }

    public Task ToggleCharacterTooltipsAsync(bool visible) { State.ShowPlayerTooltips = visible; Invalidate(); return Task.CompletedTask; }
    public Task ToggleMarkerTooltipsAsync(string type, bool visible) => Task.CompletedTask;

    #endregion

    #region IMapView — tiles

    public Task RefreshTileAsync(int mapId, int x, int y, int z, long timestamp)
    {
        tileGrid.ClearNegative(new TileGrid.TileKey(mapId, z, x, y));
        Invalidate();
        return Task.CompletedTask;
    }

    public Task ApplyTileUpdatesAsync(IReadOnlyList<TileUpdate> updates)
    {
        if (updates == null || updates.Count == 0) return Task.CompletedTask;
        foreach (var u in updates) tileGrid.ClearNegative(new TileGrid.TileKey(u.M, u.Z, u.X, u.Y));
        Invalidate();
        return Task.CompletedTask;
    }

    public Task SetMapRevisionAsync(int mapId, int revision)
    {
        Navigation.SetMapRevision(mapId, revision);
        tileGrid.ClearNegativeForMap(mapId);
        if (mapId == currentMapId) Invalidate();
        return Task.CompletedTask;
    }

    public Task SetOverlayDataAsync(int mapId, object overlays) => Task.CompletedTask;

    #endregion

    #region IMapView — jump

    public Task JumpToCharacterAsync(int characterId)
    {
        if (!characters.TryGetValue(characterId, out var c)) return Task.CompletedTask;
        return JumpToWorldPxAsync(c.Map, c.Position.X, c.Position.Y);
    }

    public Task JumpToMarkerAsync(int markerId)
    {
        if (!markers.TryGetValue(markerId, out var m)) return Task.CompletedTask;
        return JumpToWorldPxAsync(m.Map, m.Position.X, m.Position.Y);
    }

    public Task JumpToCustomMarkerAsync(int markerId, int? zoomLevel = null)
    {
        var cm = CustomMarkerState.GetCustomMarkerById(markerId);
        if (cm == null) return Task.CompletedTask;
        var worldPxX = cm.CoordX * 100 + cm.X;
        var worldPxY = cm.CoordY * 100 + cm.Y;
        if (zoomLevel is int z)
        {
            var clamped = MapMath.ClampZoom(z);
            if (clamped != viewport.Zoom) StartZoomTransition();
            viewport = viewport.WithZoom(clamped);
        }
        return JumpToWorldPxAsync(cm.MapId, worldPxX, worldPxY);
    }

    private async Task JumpToWorldPxAsync(int mapId, int worldPxX, int worldPxY)
    {
        if (mapId != currentMapId) await ChangeMapAsync(mapId);
        viewport = viewport.WithCenter(worldPxX / 100.0, worldPxY / 100.0);
        State.CenterX = viewport.CenterX;
        State.CenterY = viewport.CenterY;
        await SyncShimPanAsync();
        Invalidate();
        await OnMapDragged.InvokeAsync(((int)Math.Round(viewport.CenterX), (int)Math.Round(viewport.CenterY), viewport.Zoom));
    }

    public Task SetClusteringEnabled(bool enabled) { State.ShowClustering = enabled; Invalidate(); return Task.CompletedTask; }

    #endregion

    #region Helpers

    private static List<T>? CoerceList<T>(object data)
    {
        return data switch
        {
            null => null,
            List<T> l => l,
            IEnumerable<T> e => e.ToList(),
            JsonElement je => je.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<T>>(je.GetRawText(), JsonOpts)
                : null,
            string s => JsonSerializer.Deserialize<List<T>>(s, JsonOpts),
            _ => null,
        };
    }

    private static T? CoerceObject<T>(object data) where T : class
    {
        return data switch
        {
            null => null,
            T t => t,
            JsonElement je => JsonSerializer.Deserialize<T>(je.GetRawText(), JsonOpts),
            string s => JsonSerializer.Deserialize<T>(s, JsonOpts),
            _ => null,
        };
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        zoomTransitionTimer?.Dispose();
        zoomTransitionTimer = null;
        LayerVisibility.OnChange -= HandleServiceChanged;
        CustomMarkerState.OnChange -= HandleServiceChanged;
        if (shim != null)
        {
            try
            {
                await shim.InvokeVoidAsync("dispose", viewportEl);
                await shim.DisposeAsync();
            }
            catch { }
            shim = null;
        }
        dotnetRef?.Dispose();
        dotnetRef = null;
    }
}
