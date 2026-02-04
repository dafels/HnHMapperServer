using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service interface for managing public maps
/// </summary>
public interface IPublicMapService
{
    /// <summary>
    /// Get a public map by its slug ID
    /// </summary>
    Task<PublicMapDto?> GetPublicMapAsync(string id);

    /// <summary>
    /// Get all public maps
    /// </summary>
    Task<List<PublicMapDto>> GetAllPublicMapsAsync();

    /// <summary>
    /// Get only active public maps (for public viewers)
    /// </summary>
    Task<List<PublicMapDto>> GetActivePublicMapsAsync();

    /// <summary>
    /// Create a new public map
    /// </summary>
    Task<PublicMapDto> CreatePublicMapAsync(CreatePublicMapDto dto, string createdBy);

    /// <summary>
    /// Update a public map
    /// </summary>
    Task<PublicMapDto> UpdatePublicMapAsync(string id, UpdatePublicMapDto dto);

    /// <summary>
    /// Delete a public map and its generated tiles
    /// </summary>
    Task DeletePublicMapAsync(string id);

    /// <summary>
    /// Check if a public map exists
    /// </summary>
    Task<bool> PublicMapExistsAsync(string id);

    /// <summary>
    /// Add a source map to a public map
    /// </summary>
    Task<PublicMapSourceDto> AddSourceAsync(string publicMapId, AddPublicMapSourceDto dto, string addedBy);

    /// <summary>
    /// Remove a source map from a public map
    /// </summary>
    Task RemoveSourceAsync(string publicMapId, int sourceId);

    /// <summary>
    /// Get all sources for a public map
    /// </summary>
    Task<List<PublicMapSourceDto>> GetSourcesAsync(string publicMapId);

    /// <summary>
    /// Get available tenant maps for source selection
    /// </summary>
    Task<List<AvailableTenantMapDto>> GetAvailableTenantMapsAsync();

    /// <summary>
    /// Get public map bounds for the map viewer
    /// </summary>
    Task<PublicMapBoundsDto?> GetBoundsAsync(string id);

    /// <summary>
    /// Get generation status for a public map
    /// </summary>
    Task<GenerationStatusDto?> GetGenerationStatusAsync(string id);

    /// <summary>
    /// Generate slug from name
    /// </summary>
    string GenerateSlug(string name);

    /// <summary>
    /// Check if a slug is available
    /// </summary>
    Task<bool> IsSlugAvailableAsync(string slug);

    // ========================================
    // HMap Source Management for Public Maps
    // ========================================

    /// <summary>
    /// Add an HMap source to a public map's generation list
    /// </summary>
    Task<PublicMapHmapSourceDto> AddHmapSourceAsync(string publicMapId, AddPublicMapHmapSourceDto dto);

    /// <summary>
    /// Remove an HMap source from a public map
    /// </summary>
    Task RemoveHmapSourceAsync(string publicMapId, int hmapSourceId);

    /// <summary>
    /// Get all HMap sources selected for a public map
    /// </summary>
    Task<List<PublicMapHmapSourceDto>> GetHmapSourcesAsync(string publicMapId);

    /// <summary>
    /// Analyze source contributions for a public map (what each source adds vs overlap)
    /// </summary>
    Task<SourceContributionSummaryDto> AnalyzeSourceContributionsAsync(string publicMapId);

    /// <summary>
    /// Update priority of an HMap source in a public map
    /// </summary>
    Task UpdateHmapSourcePriorityAsync(string publicMapId, int hmapSourceId, int newPriority);
}
