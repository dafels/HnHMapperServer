using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for importing the shared PUBLIC map into a tenant's map.
///
/// Reads only the PUBLIC artifact committed by the last regeneration:
///   - WebP zoom-0 tiles under {GridStorage}/public/{publicMapId}/0/
///   - markers.json (thingwall markers in absolute coords)
///   - PublicMapGridIndex rows (per-coord opaque grid ids from snapshot time)
///
/// Never reads source tenants' Tiles/Grids/Markers — any updates a source tenant makes
/// between PUBLIC regenerations are invisible to importers until the next regeneration.
///
/// Source PUBLIC-map resolution:
///   1. If a PublicMap with id == "public" exists and is active, use it.
///   2. Otherwise if exactly one active PublicMap exists, use that one.
///   3. Otherwise return an error.
/// </summary>
public interface IPublicMapTenantImportService
{
    /// <summary>
    /// Preferred PUBLIC map id when more than one exists.
    /// </summary>
    public const string PreferredPublicMapId = "public";

    /// <summary>
    /// Run the import.
    /// </summary>
    /// <param name="targetTenantId">Tenant the import writes into.</param>
    /// <param name="targetMapId">
    /// When null, create a brand-new tenant map and dump the PUBLIC content into it (the safe
    /// default). When non-null, merge the PUBLIC into this existing tenant map: alignment delta
    /// is computed from grid ids shared between <c>PublicMapGridIndex</c> and the target map's
    /// own <c>Grids</c>; existing tiles win on collision.
    /// </param>
    /// <param name="gridStorage">Base storage path (config "GridStorage").</param>
    /// <param name="progress">Optional progress reporter. Uses the same DTO as the .hmap import.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PublicMapImportResult> ImportAsync(
        string targetTenantId,
        int? targetMapId,
        string gridStorage,
        IProgress<HmapImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
