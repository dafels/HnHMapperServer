using System.Text.RegularExpressions;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public partial class PublicMapService : IPublicMapService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<PublicMapService> _logger;
    private readonly string _gridStorage;

    public PublicMapService(
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        ILogger<PublicMapService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _gridStorage = configuration["GridStorage"] ?? "map";
    }

    public async Task<PublicMapDto?> GetPublicMapAsync(string id)
    {
        var entity = await _dbContext.PublicMaps
            .FirstOrDefaultAsync(p => p.Id == id);

        if (entity == null)
            return null;

        var sources = await GetSourcesAsync(id);
        return MapToDto(entity, sources);
    }

    public async Task<List<PublicMapDto>> GetAllPublicMapsAsync()
    {
        var entities = await _dbContext.PublicMaps
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var result = new List<PublicMapDto>();
        foreach (var entity in entities)
        {
            var sources = await GetSourcesAsync(entity.Id);
            result.Add(MapToDto(entity, sources));
        }
        return result;
    }

    public async Task<List<PublicMapDto>> GetActivePublicMapsAsync()
    {
        var entities = await _dbContext.PublicMaps
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var result = new List<PublicMapDto>();
        foreach (var entity in entities)
        {
            var sources = await GetSourcesAsync(entity.Id);
            result.Add(MapToDto(entity, sources));
        }
        return result;
    }

    public async Task<PublicMapDto> CreatePublicMapAsync(CreatePublicMapDto dto, string createdBy)
    {
        // Generate or validate slug
        var slug = string.IsNullOrWhiteSpace(dto.Slug)
            ? GenerateSlug(dto.Name)
            : GenerateSlug(dto.Slug);

        // Ensure slug is unique
        if (await _dbContext.PublicMaps.AnyAsync(p => p.Id == slug))
        {
            // Append number if slug exists
            var baseSlug = slug;
            var counter = 1;
            while (await _dbContext.PublicMaps.AnyAsync(p => p.Id == slug))
            {
                slug = $"{baseSlug}-{counter}";
                counter++;
            }
        }

        var entity = new PublicMapEntity
        {
            Id = slug,
            Name = dto.Name,
            IsActive = dto.IsActive,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            GenerationStatus = "pending",
            TileCount = 0,
            GenerationProgress = 0
        };

        _dbContext.PublicMaps.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created public map {PublicMapId} by {CreatedBy}", slug, createdBy);

        return MapToDto(entity, new List<PublicMapSourceDto>());
    }

    public async Task<PublicMapDto> UpdatePublicMapAsync(string id, UpdatePublicMapDto dto)
    {
        var entity = await _dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null)
        {
            throw new ArgumentException($"Public map {id} not found");
        }

        if (dto.Name != null)
            entity.Name = dto.Name;

        if (dto.IsActive.HasValue)
            entity.IsActive = dto.IsActive.Value;

        if (dto.AutoRegenerate.HasValue)
            entity.AutoRegenerate = dto.AutoRegenerate.Value;

        if (dto.RegenerateIntervalMinutes.HasValue)
            entity.RegenerateIntervalMinutes = dto.RegenerateIntervalMinutes.Value;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated public map {PublicMapId}", id);

        var sources = await GetSourcesAsync(id);
        return MapToDto(entity, sources);
    }

    public async Task DeletePublicMapAsync(string id)
    {
        var entity = await _dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null)
        {
            throw new ArgumentException($"Public map {id} not found");
        }

        // Delete generated tiles from disk (mandatory - fail if can't clean up)
        var publicMapPath = Path.Combine(_gridStorage, "public", id);
        if (Directory.Exists(publicMapPath))
        {
            Directory.Delete(publicMapPath, recursive: true);
            _logger.LogInformation("Deleted tile directory for public map {PublicMapId}", id);
        }

        // Explicitly delete sources first to avoid cascade delete conflicts with change tracking
        var sources = await _dbContext.PublicMapSources
            .Where(s => s.PublicMapId == id)
            .ToListAsync();
        _dbContext.PublicMapSources.RemoveRange(sources);

        _dbContext.PublicMaps.Remove(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted public map {PublicMapId}", id);
    }

    public async Task<bool> PublicMapExistsAsync(string id)
    {
        return await _dbContext.PublicMaps.AnyAsync(p => p.Id == id);
    }

    public async Task<PublicMapSourceDto> AddSourceAsync(string publicMapId, AddPublicMapSourceDto dto, string addedBy)
    {
        // Verify public map exists
        if (!await PublicMapExistsAsync(publicMapId))
        {
            throw new ArgumentException($"Public map {publicMapId} not found");
        }

        // Verify tenant exists
        if (!await _dbContext.Tenants.AnyAsync(t => t.Id == dto.TenantId))
        {
            throw new ArgumentException($"Tenant {dto.TenantId} not found");
        }

        // Verify map exists in tenant (bypass tenant filter)
        var mapExists = await _dbContext.Maps
            .IgnoreQueryFilters()
            .AnyAsync(m => m.Id == dto.MapId && m.TenantId == dto.TenantId);

        if (!mapExists)
        {
            throw new ArgumentException($"Map {dto.MapId} not found in tenant {dto.TenantId}");
        }

        // Check for duplicate
        var existingSource = await _dbContext.PublicMapSources
            .FirstOrDefaultAsync(s => s.PublicMapId == publicMapId
                                    && s.TenantId == dto.TenantId
                                    && s.MapId == dto.MapId);

        if (existingSource != null)
        {
            throw new ArgumentException("This map is already a source for this public map");
        }

        var entity = new PublicMapSourceEntity
        {
            PublicMapId = publicMapId,
            TenantId = dto.TenantId,
            MapId = dto.MapId,
            Priority = dto.Priority,
            AddedAt = DateTime.UtcNow,
            AddedBy = addedBy
        };

        _dbContext.PublicMapSources.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Added source map {TenantId}/{MapId} to public map {PublicMapId}",
            dto.TenantId, dto.MapId, publicMapId);

        // Get tenant and map names for the DTO
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == dto.TenantId);
        var map = await _dbContext.Maps.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == dto.MapId);

        return new PublicMapSourceDto
        {
            Id = entity.Id,
            PublicMapId = entity.PublicMapId,
            TenantId = entity.TenantId,
            TenantName = tenant?.Name ?? entity.TenantId,
            MapId = entity.MapId,
            MapName = map?.Name ?? $"Map {entity.MapId}",
            Priority = entity.Priority,
            AddedAt = entity.AddedAt,
            AddedBy = entity.AddedBy
        };
    }

    public async Task RemoveSourceAsync(string publicMapId, int sourceId)
    {
        var entity = await _dbContext.PublicMapSources
            .FirstOrDefaultAsync(s => s.PublicMapId == publicMapId && s.Id == sourceId);

        if (entity == null)
        {
            throw new ArgumentException($"Source {sourceId} not found for public map {publicMapId}");
        }

        _dbContext.PublicMapSources.Remove(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Removed source {SourceId} from public map {PublicMapId}", sourceId, publicMapId);
    }

    public async Task<List<PublicMapSourceDto>> GetSourcesAsync(string publicMapId)
    {
        var sources = await _dbContext.PublicMapSources
            .Where(s => s.PublicMapId == publicMapId)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.AddedAt)
            .ToListAsync();

        var result = new List<PublicMapSourceDto>();

        foreach (var source in sources)
        {
            var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == source.TenantId);
            var map = await _dbContext.Maps
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == source.MapId && m.TenantId == source.TenantId);

            result.Add(new PublicMapSourceDto
            {
                Id = source.Id,
                PublicMapId = source.PublicMapId,
                TenantId = source.TenantId,
                TenantName = tenant?.Name ?? source.TenantId,
                MapId = source.MapId,
                MapName = map?.Name ?? $"Map {source.MapId}",
                Priority = source.Priority,
                AddedAt = source.AddedAt,
                AddedBy = source.AddedBy
            });
        }

        return result;
    }

    public async Task<List<AvailableTenantMapDto>> GetAvailableTenantMapsAsync()
    {
        var result = new List<AvailableTenantMapDto>();

        var tenants = await _dbContext.Tenants.Where(t => t.IsActive).ToListAsync();

        foreach (var tenant in tenants)
        {
            var maps = await _dbContext.Maps
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenant.Id)
                .ToListAsync();

            foreach (var map in maps)
            {
                var tileCount = await _dbContext.Tiles
                    .IgnoreQueryFilters()
                    .CountAsync(t => t.TenantId == tenant.Id && t.MapId == map.Id && t.Zoom == 0);

                result.Add(new AvailableTenantMapDto
                {
                    TenantId = tenant.Id,
                    TenantName = tenant.Name,
                    MapId = map.Id,
                    MapName = map.Name,
                    TileCount = tileCount
                });
            }
        }

        return result;
    }

    public async Task<PublicMapBoundsDto?> GetBoundsAsync(string id)
    {
        var entity = await _dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null)
            return null;

        return new PublicMapBoundsDto
        {
            Id = entity.Id,
            Name = entity.Name,
            MinX = entity.MinX,
            MaxX = entity.MaxX,
            MinY = entity.MinY,
            MaxY = entity.MaxY,
            TileVersion = entity.LastGeneratedAt.HasValue
                ? new DateTimeOffset(entity.LastGeneratedAt.Value, TimeSpan.Zero).ToUnixTimeSeconds()
                : null
        };
    }

    public async Task<GenerationStatusDto?> GetGenerationStatusAsync(string id)
    {
        var entity = await _dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null)
            return null;

        return new GenerationStatusDto
        {
            PublicMapId = entity.Id,
            Status = entity.GenerationStatus,
            Progress = entity.GenerationProgress,
            TileCount = entity.TileCount,
            LastGeneratedAt = entity.LastGeneratedAt,
            LastGenerationDurationSeconds = entity.LastGenerationDurationSeconds,
            Error = entity.GenerationError
        };
    }

    public string GenerateSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "public-map";

        // Convert to lowercase
        var slug = name.ToLowerInvariant();

        // Replace spaces and special characters with hyphens
        slug = SlugInvalidChars().Replace(slug, "-");

        // Remove multiple consecutive hyphens
        slug = SlugMultipleHyphens().Replace(slug, "-");

        // Trim hyphens from start and end
        slug = slug.Trim('-');

        // Ensure minimum length
        if (slug.Length < 3)
            slug = $"map-{slug}";

        // Limit length
        if (slug.Length > 50)
            slug = slug[..50].TrimEnd('-');

        return slug;
    }

    public async Task<bool> IsSlugAvailableAsync(string slug)
    {
        return !await _dbContext.PublicMaps.AnyAsync(p => p.Id == slug);
    }

    private static PublicMapDto MapToDto(PublicMapEntity entity, List<PublicMapSourceDto> sources)
    {
        return new PublicMapDto
        {
            Id = entity.Id,
            Name = entity.Name,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            CreatedBy = entity.CreatedBy,
            AutoRegenerate = entity.AutoRegenerate,
            RegenerateIntervalMinutes = entity.RegenerateIntervalMinutes,
            GenerationStatus = entity.GenerationStatus,
            LastGeneratedAt = entity.LastGeneratedAt,
            LastGenerationDurationSeconds = entity.LastGenerationDurationSeconds,
            TileCount = entity.TileCount,
            GenerationProgress = entity.GenerationProgress,
            GenerationError = entity.GenerationError,
            MinX = entity.MinX,
            MaxX = entity.MaxX,
            MinY = entity.MinY,
            MaxY = entity.MaxY,
            Sources = sources
        };
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugInvalidChars();

    [GeneratedRegex(@"-+")]
    private static partial Regex SlugMultipleHyphens();
}
