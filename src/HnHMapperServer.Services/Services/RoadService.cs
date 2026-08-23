using System.Text.Json;
using System.Text.RegularExpressions;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service implementation for road operations with validation and authorization
/// </summary>
public partial class RoadService : IRoadService
{
    private readonly IRoadRepository _repository;
    private readonly ILogger<RoadService> _logger;

    // Validation constants
    private const int MaxNameLength = 80;
    private const int MinWaypoints = 2;
    private const int MinCoordinate = 0;
    private const int MaxCoordinate = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RoadService(
        IRoadRepository repository,
        ILogger<RoadService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get all roads for a specific map
    /// </summary>
    public async Task<List<RoadViewDto>> GetByMapIdAsync(int mapId, string currentUsername, bool isAdmin)
    {
        var roads = await _repository.GetByMapIdAsync(mapId);
        return roads.Select(r => MapToViewDto(r, currentUsername, isAdmin)).ToList();
    }

    /// <summary>
    /// Get a road by ID
    /// </summary>
    public async Task<RoadViewDto?> GetByIdAsync(int id, string currentUsername, bool isAdmin)
    {
        var road = await _repository.GetByIdAsync(id);
        return road == null ? null : MapToViewDto(road, currentUsername, isAdmin);
    }

    /// <summary>
    /// Create a new road with validation
    /// </summary>
    public async Task<RoadViewDto> CreateAsync(CreateRoadDto dto, string currentUsername)
    {
        // Validate input
        ValidateCreateDto(dto);

        // Clamp waypoint coordinates
        var clampedWaypoints = dto.Waypoints.Select(wp => new RoadWaypointDto
        {
            CoordX = wp.CoordX,
            CoordY = wp.CoordY,
            X = ClampCoordinate(wp.X),
            Y = ClampCoordinate(wp.Y)
        }).ToList();

        // Create domain model
        var now = DateTime.UtcNow;
        var road = new Road
        {
            MapId = dto.MapId,
            Name = SanitizeString(dto.Name),
            Waypoints = JsonSerializer.Serialize(clampedWaypoints, JsonOptions),
            CreatedBy = currentUsername,
            CreatedAt = now,
            UpdatedAt = now,
            Hidden = false
        };

        var created = await _repository.CreateAsync(road);
        _logger.LogInformation("Road '{Name}' created by {User} with {WaypointCount} waypoints",
            road.Name, currentUsername, clampedWaypoints.Count);

        return MapToViewDto(created, currentUsername, false);
    }

    /// <summary>
    /// Update an existing road with authorization check
    /// </summary>
    public async Task<RoadViewDto> UpdateAsync(int id, UpdateRoadDto dto, string currentUsername, bool isAdmin)
    {
        // Validate input
        ValidateUpdateDto(dto);

        // Get existing road
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Road with ID {id} not found.");
        }

        // Authorization check: must be creator or admin
        if (!isAdmin && existing.CreatedBy != currentUsername)
        {
            throw new UnauthorizedAccessException("You can only edit your own roads.");
        }

        // Clamp waypoint coordinates
        var clampedWaypoints = dto.Waypoints.Select(wp => new RoadWaypointDto
        {
            CoordX = wp.CoordX,
            CoordY = wp.CoordY,
            X = ClampCoordinate(wp.X),
            Y = ClampCoordinate(wp.Y)
        }).ToList();

        // Update fields (CreatedAt is immutable)
        existing.Name = SanitizeString(dto.Name);
        existing.Waypoints = JsonSerializer.Serialize(clampedWaypoints, JsonOptions);
        existing.Hidden = dto.Hidden;
        existing.UpdatedAt = DateTime.UtcNow;

        var updated = await _repository.UpdateAsync(existing);
        return MapToViewDto(updated, currentUsername, isAdmin);
    }

    /// <summary>
    /// Delete a road with authorization check
    /// </summary>
    public async Task DeleteAsync(int id, string currentUsername, bool isAdmin)
    {
        // Get existing road
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Road with ID {id} not found.");
        }

        // Authorization check: must be creator or admin
        if (!isAdmin && existing.CreatedBy != currentUsername)
        {
            throw new UnauthorizedAccessException("You can only delete your own roads.");
        }

        await _repository.DeleteAsync(id);
        _logger.LogInformation("Road '{Name}' (ID: {Id}) deleted by {User}", existing.Name, id, currentUsername);
    }

    // --- Validation and sanitization methods ---

    /// <summary>
    /// Validate CreateRoadDto
    /// </summary>
    private static void ValidateCreateDto(CreateRoadDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required.", nameof(dto.Name));
        }

        if (dto.Name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name cannot exceed {MaxNameLength} characters.", nameof(dto.Name));
        }

        if (dto.Waypoints == null || dto.Waypoints.Count < MinWaypoints)
        {
            throw new ArgumentException($"At least {MinWaypoints} waypoints are required.", nameof(dto.Waypoints));
        }
    }

    /// <summary>
    /// Validate UpdateRoadDto
    /// </summary>
    private static void ValidateUpdateDto(UpdateRoadDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Name is required.", nameof(dto.Name));
        }

        if (dto.Name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name cannot exceed {MaxNameLength} characters.", nameof(dto.Name));
        }

        if (dto.Waypoints == null || dto.Waypoints.Count < MinWaypoints)
        {
            throw new ArgumentException($"At least {MinWaypoints} waypoints are required.", nameof(dto.Waypoints));
        }
    }

    /// <summary>
    /// Clamp coordinates to valid range (0-100)
    /// </summary>
    private static int ClampCoordinate(int value)
    {
        return Math.Clamp(value, MinCoordinate, MaxCoordinate);
    }

    /// <summary>
    /// Sanitize string input by stripping HTML tags and trimming
    /// </summary>
    private static string SanitizeString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // First strip script tags and their content
        var sanitized = ScriptBlockRegex().Replace(input, string.Empty);

        // Then strip all remaining HTML tags
        sanitized = HtmlTagRegex().Replace(sanitized, string.Empty);

        return sanitized.Trim();
    }

    /// <summary>
    /// Parse waypoints from JSON string
    /// </summary>
    private static List<RoadWaypointDto> ParseWaypoints(string waypointsJson)
    {
        if (string.IsNullOrEmpty(waypointsJson))
        {
            return new List<RoadWaypointDto>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RoadWaypointDto>>(waypointsJson, JsonOptions)
                   ?? new List<RoadWaypointDto>();
        }
        catch
        {
            return new List<RoadWaypointDto>();
        }
    }

    /// <summary>
    /// Map domain model to view DTO with permission flags
    /// </summary>
    private static RoadViewDto MapToViewDto(Road road, string currentUsername, bool isAdmin)
    {
        return new RoadViewDto
        {
            Id = road.Id,
            MapId = road.MapId,
            Name = road.Name,
            Waypoints = ParseWaypoints(road.Waypoints),
            CreatedBy = road.CreatedBy,
            CreatedAt = road.CreatedAt,
            UpdatedAt = road.UpdatedAt,
            Hidden = road.Hidden,
            CanEdit = isAdmin || road.CreatedBy == currentUsername
        };
    }

    /// <summary>
    /// Regex for stripping HTML tags (compiled for performance)
    /// </summary>
    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex("<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();
}
