namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// Pure coordinate math for the native Blazor map. Single source of truth for
/// screen↔world conversion, visible tile range, and zoom anchoring. Everything is
/// integer-friendly and negative-coordinate safe.
/// </summary>
public static class MapMath
{
    private const int TileSize = MapViewport.TileSize;

    /// <summary>
    /// Project a world point (grid coord + local pixel offset) into viewport CSS px.
    /// </summary>
    public static (int X, int Y) WorldToScreen(MapViewport vp, int coordX, int coordY, int localX = 0, int localY = 0)
    {
        var worldPxX = coordX * TileSize + localX;
        var worldPxY = coordY * TileSize + localY;
        return WorldPxToScreen(vp, worldPxX, worldPxY);
    }

    /// <summary>
    /// Project a world pixel (zoom-0 px, signed) into viewport CSS px.
    /// </summary>
    public static (int X, int Y) WorldPxToScreen(MapViewport vp, int worldPxX, int worldPxY)
    {
        var screenX = (int)Math.Round(vp.Width / 2.0 + (double)worldPxX / vp.Scale - vp.CamPxX);
        var screenY = (int)Math.Round(vp.Height / 2.0 + (double)worldPxY / vp.Scale - vp.CamPxY);
        return (screenX, screenY);
    }

    /// <summary>
    /// Convert a world pixel (zoom-0 px, signed) to pan-layer-local CSS px.
    /// The pan layer sits inside the viewport and the shim handles the visual offset
    /// via a translate3d transform — so entity positions inside the pan layer are
    /// constant during a pan (only the pan layer's transform moves). Tile (tx, ty)
    /// at the current zoom is at (tx*TileSize, ty*TileSize) in pan-layer coords.
    /// </summary>
    public static (int X, int Y) WorldPxToPanLayer(MapViewport vp, int worldPxX, int worldPxY)
    {
        return ((int)Math.Round((double)worldPxX / vp.Scale), (int)Math.Round((double)worldPxY / vp.Scale));
    }

    /// <summary>
    /// The CSS px offset that the pan layer must apply so that the camera center
    /// (CenterX, CenterY) lands at the viewport's geometric center.
    /// </summary>
    public static (double X, double Y) PanLayerOffset(MapViewport vp)
    {
        return (vp.Width / 2.0 - vp.CamPxX, vp.Height / 2.0 - vp.CamPxY);
    }

    /// <summary>
    /// Convert viewport-relative CSS px to a world point. Returns (coordX, coordY, localX, localY)
    /// where localX/localY are in [0,99]. Negative grid coordinates are handled correctly.
    /// </summary>
    public static (int CoordX, int CoordY, int LocalX, int LocalY) ScreenToWorld(MapViewport vp, double sx, double sy)
    {
        var worldPxX = (sx - vp.Width / 2.0 + vp.CamPxX) * vp.Scale;
        var worldPxY = (sy - vp.Height / 2.0 + vp.CamPxY) * vp.Scale;
        var coordX = (int)Math.Floor(worldPxX / TileSize);
        var coordY = (int)Math.Floor(worldPxY / TileSize);
        var localX = (int)Math.Floor(worldPxX - (double)coordX * TileSize);
        var localY = (int)Math.Floor(worldPxY - (double)coordY * TileSize);
        // Clamp local to [0,99] for safety on rounding boundary.
        if (localX < 0) localX = 0; else if (localX > 99) localX = 99;
        if (localY < 0) localY = 0; else if (localY > 99) localY = 99;
        return (coordX, coordY, localX, localY);
    }

    /// <summary>
    /// Inclusive tile range covering the visible viewport plus a margin in tiles.
    /// </summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) VisibleTileRange(MapViewport vp, int marginTiles = 1)
    {
        var minX = (int)Math.Floor((vp.CamPxX - vp.Width / 2.0) / TileSize) - marginTiles;
        var minY = (int)Math.Floor((vp.CamPxY - vp.Height / 2.0) / TileSize) - marginTiles;
        var maxX = (int)Math.Floor((vp.CamPxX + vp.Width / 2.0) / TileSize) + marginTiles;
        var maxY = (int)Math.Floor((vp.CamPxY + vp.Height / 2.0) / TileSize) + marginTiles;
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Screen position (top-left) of the tile at (tx, ty) for the current zoom.
    /// </summary>
    public static (int Left, int Top) TileScreenPosition(MapViewport vp, int tx, int ty)
    {
        var left = (int)Math.Round(tx * (double)TileSize - vp.CamPxX + vp.Width / 2.0);
        var top = (int)Math.Round(ty * (double)TileSize - vp.CamPxY + vp.Height / 2.0);
        return (left, top);
    }

    /// <summary>
    /// New center such that the world point under (anchorSx, anchorSy) stays under the cursor
    /// after a zoom change. Anchor coordinates are viewport-relative CSS px.
    /// </summary>
    public static (double CenterX, double CenterY) ZoomAnchorCenter(
        MapViewport vp, int newZoom, double anchorSx, double anchorSy)
    {
        var (cx, cy, lx, ly) = ScreenToWorld(vp, anchorSx, anchorSy);
        // World pixel of the anchor in zoom-0 px.
        var worldPxX = cx * TileSize + lx;
        var worldPxY = cy * TileSize + ly;

        // For the new center to keep that world pixel under the anchor:
        //   anchorSx - W/2 = worldPxX/newScale - (newCenter*TileSize)/newScale
        //   newCenter = (worldPxX - (anchorSx - W/2) * newScale) / TileSize
        var newScale = 1 << newZoom;
        var newCenterX = (worldPxX - (anchorSx - vp.Width / 2.0) * newScale) / TileSize;
        var newCenterY = (worldPxY - (anchorSy - vp.Height / 2.0) * newScale) / TileSize;
        return (newCenterX, newCenterY);
    }

    /// <summary>Clamp a zoom request to the supported range.</summary>
    public static int ClampZoom(int z) => Math.Clamp(z, MapViewport.MinZoom, MapViewport.MaxZoom);
}
