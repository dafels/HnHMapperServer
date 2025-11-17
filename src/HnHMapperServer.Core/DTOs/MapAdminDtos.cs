namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for admin map management operations.
/// Represents a map with all properties exposed for admin editing.
/// </summary>
public class AdminMapDto
{
    /// <summary>
    /// The unique identifier for the map.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The display name of the map.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the map is hidden from normal users.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Display priority (lower numbers appear first).
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>
/// Request DTO for renaming a map.
/// </summary>
public class RenameMapRequest
{
    /// <summary>
    /// The new name for the map.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Response DTO for successful map deletion.
/// </summary>
public class DeleteMapResponse
{
    /// <summary>
    /// Success message describing what was deleted.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
