using HnHMapperServer.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace HnHMapperServer.Web.Services;

public class MapDataService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MapDataService> _logger;

    public MapDataService(IHttpClientFactory httpClientFactory, ILogger<MapDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<CharacterModel>> GetCharactersAsync()
    {
        var client = _httpClientFactory.CreateClient("API");
        var response = await client.GetAsync("/map/api/v1/characters");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch characters. Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"Request failed with status {response.StatusCode}", null, response.StatusCode);
        }

        var characters = await response.Content.ReadFromJsonAsync<List<CharacterModel>>();
        return characters ?? new List<CharacterModel>();
    }

    public async Task<List<MarkerModel>> GetMarkersAsync()
    {
        var client = _httpClientFactory.CreateClient("API");
        var response = await client.GetAsync("/map/api/v1/markers");

        if (!response.IsSuccessStatusCode)
        {
            // Debug: AuthenticationDelegatingHandler (auth failures) and the caller already log this failure
            _logger.LogDebug("Failed to fetch markers. Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"Request failed with status {response.StatusCode}", null, response.StatusCode);
        }

        var markers = await response.Content.ReadFromJsonAsync<List<MarkerModel>>();
        return markers ?? new List<MarkerModel>();
    }

    public async Task<Dictionary<string, MapInfoModel>> GetMapsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            _logger.LogDebug("Fetching maps from /map/api/maps");
            
            var response = await client.GetFromJsonAsync<List<MapInfoModel>>("/map/api/maps");

            // Convert list to dictionary with ID as key
            var dict = new Dictionary<string, MapInfoModel>();
            if (response != null)
            {
                _logger.LogInformation("Received {Count} maps from API", response.Count);
                foreach (var map in response)
                {
                    _logger.LogDebug("Map: ID={MapId}, Name={MapName}", map.ID, map.MapInfo?.Name ?? "(null)");
                    dict[map.ID.ToString()] = map;
                }
            }
            else
            {
                _logger.LogWarning("API returned null response for maps");
            }
            
            _logger.LogInformation("Returning {Count} maps in dictionary", dict.Count);
            return dict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching maps from API");
            return new Dictionary<string, MapInfoModel>();
        }
    }

    public async Task<MapConfig> GetConfigAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetFromJsonAsync<MapConfig>("/map/api/config");
            return response ?? new MapConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching config");
            return new MapConfig();
        }
    }

    public async Task<bool> WipeTileAsync(int mapId, int x, int y)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.PostAsJsonAsync("/map/api/admin/wipeTile", new
            {
                map = mapId,
                x = x,
                y = y
            });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error wiping tile at ({X}, {Y}) on map {MapId}", x, y, mapId);
            return false;
        }
    }

    public async Task<bool> SetCoordsAsync(int mapId, int fromX, int fromY, int toX, int toY)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.PostAsJsonAsync("/map/api/admin/setCoords", new
            {
                map = mapId,
                fx = fromX,
                fy = fromY,
                tx = toX,
                ty = toY
            });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting coords on map {MapId}", mapId);
            return false;
        }
    }

    public async Task<bool> HideMarkerAsync(int markerId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.PostAsJsonAsync("/map/api/admin/hideMarker", new { id = markerId });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error hiding marker {MarkerId}", markerId);
            return false;
        }
    }

    public async Task<bool> DeleteMarkerAsync(int markerId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.PostAsJsonAsync("/map/api/admin/deleteMarker", new { id = markerId });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting marker {MarkerId}", markerId);
            return false;
        }
    }

    /// <summary>
    /// Fetch overlay data (claims, villages, provinces) for the specified grid coordinates.
    /// </summary>
    /// <param name="mapId">The map ID to fetch overlays for</param>
    /// <param name="coords">Comma-separated coordinates in format "x1_y1,x2_y2,..."</param>
    /// <returns>List of overlay data with base64-encoded bitpacked data</returns>
    public async Task<List<OverlayDataDto>> GetOverlaysAsync(int mapId, string coords)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync($"/map/api/v1/overlays?mapId={mapId}&coords={Uri.EscapeDataString(coords)}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch overlays. Status: {StatusCode}", response.StatusCode);
                return new List<OverlayDataDto>();
            }

            var overlays = await response.Content.ReadFromJsonAsync<List<OverlayDataDto>>();
            return overlays ?? new List<OverlayDataDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching overlays for map {MapId}", mapId);
            return new List<OverlayDataDto>();
        }
    }
}

/// <summary>
/// DTO for overlay data returned from the API
/// </summary>
public class OverlayDataDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty; // Base64-encoded bitpacked data
}
