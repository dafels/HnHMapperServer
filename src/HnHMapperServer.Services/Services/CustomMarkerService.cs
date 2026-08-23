using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service implementation for custom marker operations with validation and authorization
/// </summary>
public partial class CustomMarkerService : ICustomMarkerService
{
    private readonly ICustomMarkerRepository _repository;
    private readonly ApplicationDbContext _context;
    private readonly IIconCatalogService _iconCatalog;
    private readonly ILogger<CustomMarkerService> _logger;
    private static bool _queryPlanLogged;

    // Validation constants
    private const int MaxTitleLength = 80;
    private const int MaxDescriptionLength = 1000;
    private const int MinCoordinate = 0;
    private const int MaxCoordinate = 100;

    public CustomMarkerService(
        ICustomMarkerRepository repository,
        ApplicationDbContext context,
        IIconCatalogService iconCatalog,
        ILogger<CustomMarkerService> logger)
    {
        _repository = repository;
        _context = context;
        _iconCatalog = iconCatalog;
        _logger = logger;
    }

    /// <summary>
    /// Get all custom markers for a specific map
    /// </summary>
    public async Task<List<CustomMarkerViewDto>> GetByMapIdAsync(int mapId, string currentUsername, bool isAdmin)
    {
        var isDevelopment = IsDevelopmentEnvironment();
        Stopwatch? stopwatch = null;

        if (isDevelopment)
        {
            stopwatch = Stopwatch.StartNew();
            if (!_queryPlanLogged)
            {
                await LogQueryPlanOnceAsync();
            }
        }

        var markers = await _repository.GetByMapIdAsync(mapId);

        if (stopwatch is not null)
        {
            stopwatch.Stop();
            _logger.LogDebug("Loaded {Count} custom markers for map {MapId} in {ElapsedMs:F2} ms", markers.Count, mapId, stopwatch.Elapsed.TotalMilliseconds);
        }

        return markers.Select(m => MapToViewDto(m, currentUsername, isAdmin)).ToList();
    }

    /// <summary>
    /// Get a custom marker by ID
    /// </summary>
    public async Task<CustomMarkerViewDto?> GetByIdAsync(int id, string currentUsername, bool isAdmin)
    {
        var marker = await _repository.GetByIdAsync(id);
        return marker == null ? null : MapToViewDto(marker, currentUsername, isAdmin);
    }

    /// <summary>
    /// Create a new custom marker with validation
    /// </summary>
    public async Task<CustomMarkerViewDto> CreateAsync(CreateCustomMarkerDto dto, string currentUsername)
    {
        // Validate input
        ValidateCreateDto(dto);

        // Validate icon against whitelist
        var availableIcons = await _iconCatalog.GetIconsAsync();
        if (!availableIcons.Contains(dto.Icon))
        {
            throw new ArgumentException($"Icon '{dto.Icon}' is not in the allowed icon list.");
        }

        // Resolve GridId from coordinates
        var grid = await _context.Grids
            .FirstOrDefaultAsync(g => g.Map == dto.MapId && g.CoordX == dto.CoordX && g.CoordY == dto.CoordY);

        if (grid == null)
        {
            throw new InvalidOperationException($"Grid not found at coordinates ({dto.CoordX}, {dto.CoordY}) for map {dto.MapId}");
        }

        // Create domain model
        var now = DateTime.UtcNow;
        var marker = new CustomMarker
        {
            MapId = dto.MapId,
            GridId = grid.Id,
            CoordX = dto.CoordX,
            CoordY = dto.CoordY,
            X = ClampCoordinate(dto.X),
            Y = ClampCoordinate(dto.Y),
            Title = SanitizeString(dto.Title),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : SanitizeString(dto.Description),
            Icon = dto.Icon,
            CreatedBy = currentUsername,
            PlacedAt = now,
            UpdatedAt = now,
            Hidden = false
        };

        var created = await _repository.CreateAsync(marker);
        return MapToViewDto(created, currentUsername, false);
    }

    /// <summary>
    /// Update an existing custom marker with authorization check
    /// </summary>
    public async Task<CustomMarkerViewDto> UpdateAsync(int id, UpdateCustomMarkerDto dto, string currentUsername, bool isAdmin)
    {
        // Validate input
        ValidateUpdateDto(dto);

        // Get existing marker
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Custom marker with ID {id} not found.");
        }

        // Authorization check: must be creator or admin
        if (!isAdmin && existing.CreatedBy != currentUsername)
        {
            throw new UnauthorizedAccessException("You can only edit your own custom markers.");
        }

        // Validate icon against whitelist
        var availableIcons = await _iconCatalog.GetIconsAsync();
        if (!availableIcons.Contains(dto.Icon))
        {
            throw new ArgumentException($"Icon '{dto.Icon}' is not in the allowed icon list.");
        }

        // Update fields (PlacedAt is immutable)
        existing.Title = SanitizeString(dto.Title);
        existing.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : SanitizeString(dto.Description);
        existing.Icon = dto.Icon;
        existing.Hidden = dto.Hidden;
        existing.UpdatedAt = DateTime.UtcNow;

        var updated = await _repository.UpdateAsync(existing);
        return MapToViewDto(updated, currentUsername, isAdmin);
    }

    /// <summary>
    /// Delete a custom marker with authorization check
    /// </summary>
    public async Task DeleteAsync(int id, string currentUsername, bool isAdmin)
    {
        // Get existing marker
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Custom marker with ID {id} not found.");
        }

        // Authorization check: must be creator or admin
        if (!isAdmin && existing.CreatedBy != currentUsername)
        {
            throw new UnauthorizedAccessException("You can only delete your own custom markers.");
        }

        await _repository.DeleteAsync(id);
    }

    /// <summary>
    /// Get list of available marker icons from the icon catalog service
    /// </summary>
    public async Task<List<string>> GetAvailableIconsAsync()
    {
        return await _iconCatalog.GetIconsAsync();
    }

    private async Task LogQueryPlanOnceAsync()
    {
        if (_queryPlanLogged)
        {
            return;
        }

        try
        {
            var connection = _context.Database.GetDbConnection();
            var shouldClose = connection.State == ConnectionState.Closed;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM CustomMarkers WHERE MapId = $mapId ORDER BY PlacedAt DESC";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$mapId";
            parameter.Value = -1;
            command.Parameters.Add(parameter);

            var sb = new StringBuilder();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var line = reader.GetString(reader.FieldCount - 1);
                sb.AppendLine(line);
            }

            _logger.LogDebug("Custom marker query plan:{NewLine}{Plan}", Environment.NewLine, sb.ToString().TrimEnd());
            _queryPlanLogged = true;

            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to log custom marker query plan.");
        }
    }

    private static bool IsDevelopmentEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                   ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(value, "Development", StringComparison.OrdinalIgnoreCase);
    }

    // --- Validation and sanitization methods ---

    /// <summary>
    /// Validate CreateCustomMarkerDto
    /// </summary>
    private static void ValidateCreateDto(CreateCustomMarkerDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            throw new ArgumentException("Title is required.", nameof(dto.Title));
        }

        if (dto.Title.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Title cannot exceed {MaxTitleLength} characters.", nameof(dto.Title));
        }

        if (dto.Description?.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"Description cannot exceed {MaxDescriptionLength} characters.", nameof(dto.Description));
        }

        if (string.IsNullOrWhiteSpace(dto.Icon))
        {
            throw new ArgumentException("Icon is required.", nameof(dto.Icon));
        }

        // Note: Coordinates are clamped, not validated (see ClampCoordinate method)
    }

    /// <summary>
    /// Validate UpdateCustomMarkerDto
    /// </summary>
    private static void ValidateUpdateDto(UpdateCustomMarkerDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            throw new ArgumentException("Title is required.", nameof(dto.Title));
        }

        if (dto.Title.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Title cannot exceed {MaxTitleLength} characters.", nameof(dto.Title));
        }

        if (dto.Description?.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"Description cannot exceed {MaxDescriptionLength} characters.", nameof(dto.Description));
        }

        if (string.IsNullOrWhiteSpace(dto.Icon))
        {
            throw new ArgumentException("Icon is required.", nameof(dto.Icon));
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

        // First strip script tags and their content (must be done before general tag stripping)
        var sanitized = ScriptBlockRegex().Replace(input, string.Empty);
        
        // Then strip all remaining HTML tags
        sanitized = HtmlTagRegex().Replace(sanitized, string.Empty);
        
        return sanitized.Trim();
    }

    /// <summary>
    /// Map domain model to view DTO with permission flags
    /// </summary>
    private static CustomMarkerViewDto MapToViewDto(CustomMarker marker, string currentUsername, bool isAdmin)
    {
        return new CustomMarkerViewDto
        {
            Id = marker.Id,
            MapId = marker.MapId,
            CoordX = marker.CoordX,
            CoordY = marker.CoordY,
            X = marker.X,
            Y = marker.Y,
            Title = marker.Title,
            Description = marker.Description,
            Icon = marker.Icon,
            CreatedBy = marker.CreatedBy,
            PlacedAt = marker.PlacedAt,
            UpdatedAt = marker.UpdatedAt,
            Hidden = marker.Hidden,
            CanEdit = isAdmin || marker.CreatedBy == currentUsername
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

