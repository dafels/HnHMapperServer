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
