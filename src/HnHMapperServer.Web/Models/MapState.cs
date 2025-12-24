namespace HnHMapperServer.Web.Models;

public class MapState
{
    public int CurrentMapId { get; set; }
    public int? OverlayMapId { get; set; }
    public double OverlayOffsetX { get; set; }
    public double OverlayOffsetY { get; set; }
    public int? TrackingCharacterId { get; set; }

    // Visibility toggles
    public bool ShowGridCoordinates { get; set; }
    public bool ShowMarkers { get; set; } = false;
    public bool ShowCustomMarkers { get; set; } = true;
    public bool ShowThingwalls { get; set; } = true;
    public bool ShowQuests { get; set; }
    public bool ShowPlayers { get; set; } = true;
    public bool ShowClustering { get; set; } = true;

    // Tooltip toggles
    public bool ShowThingwallTooltips { get; set; } = true;
    public bool ShowQuestTooltips { get; set; }
    public bool ShowPlayerTooltips { get; set; } = true;

    // Current view
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public int Zoom { get; set; } = 1;

    // User permissions
    public List<string> Permissions { get; set; } = new();
    public string TenantRole { get; set; } = "";

    // Update interval
    public int UpdateIntervalMs { get; set; } = 2000;
}
