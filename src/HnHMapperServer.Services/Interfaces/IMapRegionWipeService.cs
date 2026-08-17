using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Superadmin repair tool: surgically removes a rectangular region of one map — grids, the
/// markers hanging on them (plus marker-attached timers), zoom-0 tiles and overlay rows — so a
/// corrupted area can be re-mapped cleanly by the next player who walks it. Zoom 1-6 tile rows
/// are deliberately left in place (stale imagery heals as later uploads regenerate the pyramid).
/// </summary>
public interface IMapRegionWipeService
{
    /// <summary>Counts what a wipe of the (inclusive) box would remove, without deleting anything.</summary>
    Task<MapRegionWipePreviewDto> PreviewAsync(string tenantId, int mapId, int x1, int y1, int x2, int y2, CancellationToken ct = default);

    /// <summary>Deletes the region's rows in one transaction, then best-effort deletes the zoom-0 tile files.</summary>
    Task<MapRegionWipeResultDto> WipeAsync(string tenantId, int mapId, int x1, int y1, int x2, int y2, CancellationToken ct = default);
}
