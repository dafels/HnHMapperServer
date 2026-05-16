using HnHMapperServer.Web.Models;

namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// Common surface for the map renderer. Implemented by both the legacy Leaflet-based
/// <see cref="MapView"/> and the native Blazor <c>BlazorMapView</c>. Lets the parent
/// page swap implementations via <c>@ref</c> without touching its handler code.
/// </summary>
public interface IMapView
{
    bool IsInitialized { get; }

    Task ChangeMapAsync(int mapId);
    Task SetOverlayMapAsync(int? mapId, double offsetX = 0, double offsetY = 0);
    Task SetOverlayOffsetAsync(double offsetX, double offsetY);
    Task SetViewAsync(int gridX, int gridY, int zoom);
    Task ZoomOutAsync();
    Task ToggleGridCoordinatesAsync(bool visible);
    Task RefreshTilesAsync();

    Task AddCharacterAsync(CharacterModel character);
    Task UpdateCharacterAsync(CharacterModel character);
    Task RemoveCharacterAsync(int characterId);
    Task ClearAllCharactersAsync();
    Task SetCharactersSnapshotAsync(object charactersData);
    Task ApplyCharacterDeltaAsync(object delta);

    Task AddMarkerAsync(MarkerModel marker);
    Task AddMarkersAsync(IEnumerable<MarkerModel> markers);
    Task UpdateMarkerAsync(MarkerModel marker);
    Task RemoveMarkerAsync(int markerId);
    Task ClearAllMarkersAsync();
    Task SetHiddenMarkerTypesAsync(IEnumerable<string> hiddenTypes);

    Task AddCustomMarkerAsync(CustomMarkerViewModel marker);
    Task UpdateCustomMarkerAsync(CustomMarkerViewModel marker);
    Task RemoveCustomMarkerAsync(int markerId);
    Task ClearAllCustomMarkersAsync();

    Task ToggleCharacterTooltipsAsync(bool visible);
    Task ToggleMarkerTooltipsAsync(string type, bool visible);

    Task RefreshTileAsync(int mapId, int x, int y, int z, long timestamp);
    Task ApplyTileUpdatesAsync(IReadOnlyList<TileUpdate> updates);

    Task JumpToCharacterAsync(int characterId);
    Task JumpToMarkerAsync(int markerId);
    Task JumpToCustomMarkerAsync(int markerId, int? zoomLevel = null);

    Task SetClusteringEnabled(bool enabled);
    Task SetMapRevisionAsync(int mapId, int revision);
    Task SetOverlayDataAsync(int mapId, object overlays);
}
