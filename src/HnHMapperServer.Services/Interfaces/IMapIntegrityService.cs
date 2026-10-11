using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Cross-tenant map integrity scan for superadmins: finds cells claimed by more than one grid id
/// (the fingerprint of coordinate-frame corruption — two world regions mapped onto the same
/// coordinates) and legacy placeholder ("0") grid rows. Read-only; repair is the wipe-region tool.
/// </summary>
public interface IMapIntegrityService
{
    Task<MapIntegrityReportDto> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes one tenant's orphaned map storage: tile rows on dead map ids, dead-map tile
    /// directories (legacy + WebP trees), and pool PNGs referenced by nothing, then recalculates
    /// the tenant's storage usage. Only provably-dead data is touched — anything a live row or
    /// grid still references is kept.
    /// </summary>
    Task<OrphanPurgeResultDto> PurgeOrphanedMapDataAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Non-destructive repair of one tenant's live zoom-0 rows whose source PNG is gone: a row
    /// whose cell has exactly one owner grid with a pool PNG is repointed to it (File, size,
    /// Cache = now); the owner of a row without one is re-armed (NextUpdate in the past) so the
    /// next client re-uploads the grid; contested cells and grid ids that are not plain file names
    /// are skipped. Returns the restore keys (computed before anything changed), the repointed
    /// coords for the caller to mark dirty and — separately — those it may redraw now (cells with
    /// no row still missing), the ids of the rows nothing can recover — deleted only by
    /// <see cref="ForgetMissingTileSourceRowsAsync"/> — and those of them the restore keys cannot
    /// rebuild, for the forget's audit entry.
    /// </summary>
    Task<MissingTileSourcesRepairResultDto> RepairMissingTileSourcesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the given zoom-0 rows of the tenant whose source file is still missing (a row whose
    /// file was restored meanwhile is kept). Returns the rows deleted.
    /// </summary>
    Task<int> ForgetMissingTileSourceRowsAsync(string tenantId, IReadOnlyCollection<int> tileRowIds, CancellationToken ct = default);
}
