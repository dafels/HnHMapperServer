namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for public map information
/// </summary>
public class PublicMapDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    // Generation settings
    public bool AutoRegenerate { get; set; }
    public int? RegenerateIntervalMinutes { get; set; }

    // Generation status
    public string GenerationStatus { get; set; } = "pending";
    public DateTime? LastGeneratedAt { get; set; }
    public int? LastGenerationDurationSeconds { get; set; }
    public int TileCount { get; set; }
    public int GenerationProgress { get; set; }
    public string? GenerationError { get; set; }

    // Cached bounds
    public int? MinX { get; set; }
    public int? MaxX { get; set; }
    public int? MinY { get; set; }
    public int? MaxY { get; set; }

    // Related data
    public List<PublicMapSourceDto> Sources { get; set; } = new();

    // Computed properties
    public string PublicUrl => $"/public/{Id}";
}

/// <summary>
/// DTO for public map source information
/// </summary>
public class PublicMapSourceDto
{
    public int Id { get; set; }
    public string PublicMapId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime AddedAt { get; set; }
    public string AddedBy { get; set; } = string.Empty;
}

/// <summary>
/// DTO for creating a new public map
/// </summary>
public class CreatePublicMapDto
{
    /// <summary>
    /// Display name for the public map
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional custom slug (auto-generated from name if not provided)
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// Whether the public map is active immediately
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO for updating a public map
/// </summary>
public class UpdatePublicMapDto
{
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
    public bool? AutoRegenerate { get; set; }
    public int? RegenerateIntervalMinutes { get; set; }
}

/// <summary>
/// DTO for adding a source to a public map
/// </summary>
public class AddPublicMapSourceDto
{
    public string TenantId { get; set; } = string.Empty;
    public int MapId { get; set; }
    public int Priority { get; set; }
}

/// <summary>
/// DTO for generation status updates
/// </summary>
public class GenerationStatusDto
{
    public string PublicMapId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public int Progress { get; set; }
    public int TileCount { get; set; }
    public DateTime? LastGeneratedAt { get; set; }
    public int? LastGenerationDurationSeconds { get; set; }
    public string? Error { get; set; }
    public bool IsRunning => Status == "running";
}

/// <summary>
/// DTO for listing available tenant maps for source selection
/// </summary>
public class AvailableTenantMapDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int TileCount { get; set; }
}

/// <summary>
/// DTO for public map bounds (for map viewer)
/// </summary>
public class PublicMapBoundsDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? MinX { get; set; }
    public int? MaxX { get; set; }
    public int? MinY { get; set; }
    public int? MaxY { get; set; }
    public bool HasBounds => MinX.HasValue && MaxX.HasValue && MinY.HasValue && MaxY.HasValue;

    /// <summary>
    /// Unix timestamp of LastGeneratedAt for browser cache busting.
    /// Appended as ?v= query param to tile URLs.
    /// </summary>
    public long? TileVersion { get; set; }
}

/// <summary>
/// DTO for thingwall markers on public maps.
/// Coordinates are absolute pixel positions (gridCoord * 100 + position within grid).
/// </summary>
public class PublicMapMarkerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public string Image { get; set; } = string.Empty;
}

// ========================================
// HMap Source Library DTOs
// ========================================

/// <summary>
/// DTO for HMap source information
/// </summary>
public class HmapSourceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }

    // Cached analysis
    public int? TotalGrids { get; set; }
    public int? SegmentCount { get; set; }
    public int? MinX { get; set; }
    public int? MaxX { get; set; }
    public int? MinY { get; set; }
    public int? MaxY { get; set; }
    public DateTime? AnalyzedAt { get; set; }

    // Computed
    public string FileSizeDisplay => FormatFileSize(FileSizeBytes);
    public bool IsAnalyzed => AnalyzedAt.HasValue;

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

/// <summary>
/// DTO for uploading a new HMap source
/// </summary>
public class UploadHmapSourceDto
{
    /// <summary>
    /// Display name for the HMap source (defaults to filename if not provided)
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// DTO for HMap source analysis result
/// </summary>
public class HmapSourceAnalysisDto
{
    public int Id { get; set; }
    public int TotalGrids { get; set; }
    public int SegmentCount { get; set; }
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }
    public List<HmapSegmentInfo> Segments { get; set; } = new();
}

/// <summary>
/// Information about a segment in an HMap file
/// </summary>
public class HmapSegmentInfo
{
    public long SegmentId { get; set; }
    public int GridCount { get; set; }
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }
}

/// <summary>
/// DTO for linking an HMap source to a public map
/// </summary>
public class PublicMapHmapSourceDto
{
    public int Id { get; set; }
    public string PublicMapId { get; set; } = string.Empty;
    public int HmapSourceId { get; set; }
    public string HmapSourceName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime AddedAt { get; set; }

    // Source info
    public int? TotalGrids { get; set; }
    public int? MinX { get; set; }
    public int? MaxX { get; set; }
    public int? MinY { get; set; }
    public int? MaxY { get; set; }

    // Per-source contribution analysis
    public int? NewGrids { get; set; }
    public int? OverlappingGrids { get; set; }
}

/// <summary>
/// DTO for adding an HMap source to a public map
/// </summary>
public class AddPublicMapHmapSourceDto
{
    public int HmapSourceId { get; set; }
    public int Priority { get; set; }
}

/// <summary>
/// Summary of source contributions for a public map
/// </summary>
public class SourceContributionSummaryDto
{
    public string PublicMapId { get; set; } = string.Empty;
    public int TotalSources { get; set; }
    public int TotalUniqueGrids { get; set; }
    public int TotalOverlappingGrids { get; set; }
    public List<SourceContributionDto> Sources { get; set; } = new();
}

/// <summary>
/// Contribution details for a single source
/// </summary>
public class SourceContributionDto
{
    public int HmapSourceId { get; set; }
    public string HmapSourceName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int TotalGrids { get; set; }
    public int NewGrids { get; set; }
    public int OverlappingGrids { get; set; }
}

// ========================================
// Pre-merge alignment analysis DTOs
// ========================================

/// <summary>
/// Result of the order-independent pre-merge analysis: how the registered tenant sources will be
/// woven into landmasses and laid out, surfaced for an admin who cannot see the maps.
/// </summary>
public class PublicMapAnalysisReportDto
{
    public string PublicMapId { get; set; } = string.Empty;
    public DateTime AnalyzedAt { get; set; }

    /// <summary>Fingerprint of the source set + grid content this analysis was computed against.
    /// If it differs at generation time, sources changed since the preview.</summary>
    public string AlignmentHash { get; set; } = string.Empty;

    public int TotalSources { get; set; }
    public int TotalGrids { get; set; }

    /// <summary>Number of separate landmasses (connected components).</summary>
    public int ClusterCount { get; set; }

    /// <summary>Sources that share no grids with anything (each its own standalone landmass).</summary>
    public int StandaloneCount { get; set; }

    public int WarningCount { get; set; }

    // Estimated unified bounds + tile cost (so the admin sees scope before a full regen).
    public int? EstMinX { get; set; }
    public int? EstMaxX { get; set; }
    public int? EstMinY { get; set; }
    public int? EstMaxY { get; set; }
    public int EstZoom0TileCount { get; set; }
    public int EstTotalTileCount { get; set; }

    public List<AlignmentClusterDto> Clusters { get; set; } = new();
    public List<AlignmentPairDto> Pairs { get; set; } = new();
    public List<AlignmentConflictDto> Conflicts { get; set; } = new();

    public bool HasConflicts => WarningCount > 0;
}

/// <summary>One landmass: the sources that align together (or a single standalone source).</summary>
public class AlignmentClusterDto
{
    public int Index { get; set; }
    public bool IsStandalone { get; set; }
    public int GridCount { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>1.0 = every overlap loop closes; &lt;1.0 = contradictory overlaps were found.</summary>
    public double Confidence { get; set; }
    public int MaxResidual { get; set; }

    public List<AlignmentSourceRefDto> Sources { get; set; } = new();
}

/// <summary>A source as a member of a landmass, with friendly names and its resolved offset.</summary>
public class AlignmentSourceRefDto
{
    /// <summary>"Tenant" or "Hmap".</summary>
    public string SourceType { get; set; } = "Tenant";
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    /// <summary>Set for hmap sources.</summary>
    public int? HmapSourceId { get; set; }
    public int GridCount { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
}

/// <summary>How a pair of sources relate via shared grid ids (accepted = they overlap and align).</summary>
public class AlignmentPairDto
{
    public string SourceAName { get; set; } = string.Empty;
    public string SourceBName { get; set; } = string.Empty;
    public int SharedGridCount { get; set; }
    public int ConsensusOffsetX { get; set; }
    public int ConsensusOffsetY { get; set; }

    /// <summary>Fraction of shared grids agreeing on the dominant offset (alignment confidence).</summary>
    public double Confidence { get; set; }
    public bool Accepted { get; set; }

    /// <summary>"insufficient_matches" or "contradiction" when not accepted.</summary>
    public string? RejectReason { get; set; }
}

/// <summary>A reportable problem: a contradictory overlap or an inconsistent alignment loop.</summary>
public class AlignmentConflictDto
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? SourceA { get; set; }
    public string? SourceB { get; set; }
    public int Residual { get; set; }
}
