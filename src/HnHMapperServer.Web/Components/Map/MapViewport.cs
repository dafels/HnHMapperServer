namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// Snapshot of the visible map area: size in CSS px and camera position in grid coords.
/// </summary>
public readonly record struct MapViewport(
    int Width,
    int Height,
    int Zoom,
    double CenterX,
    double CenterY)
{
    /// <summary>Tile size in CSS px regardless of zoom (PNG endpoint returns 100×100).</summary>
    public const int TileSize = 100;

    public const int MinZoom = 0;
    public const int MaxZoom = 6;

    /// <summary>Number of zoom-0 tiles covered by one tile at the current zoom (2^z).</summary>
    public int Scale => 1 << Zoom;

    /// <summary>Camera X in CSS px at the current zoom.</summary>
    public double CamPxX => (CenterX * TileSize) / Scale;

    /// <summary>Camera Y in CSS px at the current zoom.</summary>
    public double CamPxY => (CenterY * TileSize) / Scale;

    public MapViewport WithSize(int width, int height) => this with { Width = width, Height = height };
    public MapViewport WithCenter(double cx, double cy) => this with { CenterX = cx, CenterY = cy };
    public MapViewport WithZoom(int zoom) => this with { Zoom = zoom };
}
