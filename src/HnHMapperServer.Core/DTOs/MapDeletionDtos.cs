namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// What deleting one map removed (rows and files), shared by the tenant-admin and superadmin
/// map deletes. Files still referenced by another live map's tile rows are kept and counted.
/// </summary>
public class MapDeletionResultDto
{
    public string TenantId { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;

    public int Grids { get; set; }
    public int Tiles { get; set; }
    public int Markers { get; set; }
    public int CustomMarkers { get; set; }
    public int Roads { get; set; }
    public int Pings { get; set; }

    /// <summary>OverlayData rows plus OverlayOffsets rows referencing the map either way.</summary>
    public int Overlays { get; set; }

    /// <summary>Pending DirtyZoomTiles + DirtyWebpCells marks of the map.</summary>
    public int DirtyMarks { get; set; }

    /// <summary>Tile PNGs (zoom-0 sources, legacy zoom pyramid, grid pool) deleted from disk.</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Tile PNGs kept because a tile row of another live map still references them.</summary>
    public int FilesKeptShared { get; set; }

    /// <summary>WebP pyramid files deleted with tenants/{t}/large/{mapId}.</summary>
    public int WebpFilesDeleted { get; set; }

    public long BytesFreed { get; set; }
    public double MegabytesFreed => Math.Round(BytesFreed / 1024.0 / 1024.0, 2);

    /// <summary>Non-fatal problems (a locked file, a kept directory, a path outside the tenant).</summary>
    public List<string> Warnings { get; set; } = new();
}
