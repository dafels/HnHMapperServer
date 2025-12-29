namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Unified polling response containing all real-time data types.
/// Used as fallback when SSE connections fail (e.g., VPN users).
/// </summary>
public class PollResponseDto
{
    /// <summary>
    /// Server timestamp for tracking polling state (Unix milliseconds)
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Tile updates (delta if 'since' parameter provided)
    /// </summary>
    public List<TileCacheDto> Tiles { get; set; } = new();

    /// <summary>
    /// All active characters (requires Pointer permission, null if no permission)
    /// </summary>
    public List<CharacterDto>? Characters { get; set; }

    /// <summary>
    /// Map ID to revision number mapping for cache invalidation
    /// </summary>
    public Dictionary<int, int> MapRevisions { get; set; } = new();
}
