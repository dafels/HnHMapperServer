namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Preview of what a PUBLIC map import would do, returned by the preview endpoint.
/// Used by the UI to show a discreet confirmation summary before the actual import runs.
///
/// Intentionally aggregate-only: no per-source breakdown, no source tenant names.
/// </summary>
public class PublicMapImportPreview
{
    /// <summary>The id of the public map that will be imported from.</summary>
    public string PublicMapId { get; set; } = string.Empty;

    /// <summary>The display name of the public map.</summary>
    public string PublicMapName { get; set; } = string.Empty;

    /// <summary>The public map's last-generated timestamp (UTC), if any.</summary>
    public DateTime? LastGeneratedAt { get; set; }

    public int? MinX { get; set; }
    public int? MaxX { get; set; }
    public int? MinY { get; set; }
    public int? MaxY { get; set; }

    /// <summary>Count of indexed base grids in the PUBLIC snapshot.</summary>
    public int IndexedGridCount { get; set; }

    /// <summary>Total zoom-0 WebP tile files in the PUBLIC output directory.</summary>
    public int TileCount { get; set; }

    /// <summary>Sum of zoom-0 WebP file sizes (upper bound for the importing tenant's storage delta).</summary>
    public double EstimatedSizeMB { get; set; }

    /// <summary>Target tenant's current storage usage, in MB.</summary>
    public double TenantCurrentStorageMB { get; set; }

    /// <summary>Target tenant's storage quota, in MB.</summary>
    public double TenantStorageQuotaMB { get; set; }

    /// <summary>True when the estimated size would exceed the tenant's remaining quota.</summary>
    public bool QuotaWouldExceed => (TenantCurrentStorageMB + EstimatedSizeMB) > TenantStorageQuotaMB;

    /// <summary>
    /// True if the PUBLIC artifact has an index populated. When false, an import will fail with
    /// <c>index_missing</c> — a SuperAdmin needs to trigger a PUBLIC regeneration.
    /// </summary>
    public bool IndexAvailable => IndexedGridCount > 0;
}

/// <summary>
/// Result of importing the shared PUBLIC map into a tenant's map.
/// </summary>
public class PublicMapImportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Machine-readable error code used by the UI to drive the recovery flow:
    /// "lock_held", "no_public_map", "ambiguous_public_map", "index_missing",
    /// "no_target_map", "no_overlap", "quota_exceeded", "import_failed", "canceled".
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The map id the import wrote into. For createNew this is the new map id.
    /// </summary>
    public int TargetMapId { get; set; }

    /// <summary>
    /// The id of the public map that was actually imported from (resolved at run time).
    /// </summary>
    public string? SourcePublicMapId { get; set; }

    /// <summary>True when this import created a brand-new MapInfo in the target tenant.</summary>
    public bool CreatedNewMap { get; set; }

    public int TilesAdded { get; set; }
    public int TilesSkipped { get; set; }
    public int MarkersAdded { get; set; }
    public int MarkersSkipped { get; set; }

    /// <summary>Bytes written into the target tenant's storage.</summary>
    public long BytesAdded { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>Grid ids inserted in the target tenant — used by rollback on failure.</summary>
    public List<string> CreatedGridIds { get; set; } = new();

    /// <summary>Marker ids inserted in the target tenant — used by rollback on failure.</summary>
    public List<int> CreatedMarkerIds { get; set; } = new();
}
