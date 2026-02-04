using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service for managing the HMap source library
/// </summary>
public class HmapSourceService : IHmapSourceService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<HmapSourceService> _logger;
    private readonly string _gridStorage;
    private readonly string _hmapSourcesPath;

    private const string HMAP_SOURCES_DIR = "hmap-sources";
    private const long MAX_FILE_SIZE = 500 * 1024 * 1024; // 500 MB

    public HmapSourceService(
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        ILogger<HmapSourceService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _gridStorage = configuration["GridStorage"] ?? "map";
        _hmapSourcesPath = Path.Combine(_gridStorage, HMAP_SOURCES_DIR);

        // Ensure the hmap-sources directory exists
        if (!Directory.Exists(_hmapSourcesPath))
        {
            Directory.CreateDirectory(_hmapSourcesPath);
        }
    }

    public async Task<HmapSourceDto> UploadAsync(Stream hmapStream, string fileName, string? name, string? uploadedBy)
    {
        // Validate file extension
        if (!fileName.EndsWith(".hmap", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File must have .hmap extension");
        }

        // Read file into memory to get size and validate format
        using var ms = new MemoryStream();
        await hmapStream.CopyToAsync(ms);
        var fileSize = ms.Length;

        if (fileSize > MAX_FILE_SIZE)
        {
            throw new ArgumentException($"File size exceeds maximum of {MAX_FILE_SIZE / (1024 * 1024)} MB");
        }

        if (fileSize < 15) // Minimum for signature
        {
            throw new ArgumentException("File is too small to be a valid HMap file");
        }

        // Validate HMap signature
        ms.Position = 0;
        var sigBytes = new byte[15];
        await ms.ReadExactlyAsync(sigBytes);
        var signature = System.Text.Encoding.ASCII.GetString(sigBytes);

        if (signature != "Haven Mapfile 1")
        {
            throw new ArgumentException("Invalid HMap file format - signature mismatch");
        }

        // Generate unique filename
        var uniqueFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Path.GetFileNameWithoutExtension(fileName)}.hmap";
        var filePath = Path.Combine(HMAP_SOURCES_DIR, uniqueFileName);
        var fullPath = Path.Combine(_gridStorage, filePath);

        // Write file to disk
        ms.Position = 0;
        await using var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
        await ms.CopyToAsync(fileStream);

        // Create database entry
        var entity = new HmapSourceEntity
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name,
            FileName = fileName,
            FilePath = filePath,
            FileSizeBytes = fileSize,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = uploadedBy
        };

        _dbContext.HmapSources.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Uploaded HMap source {SourceId} ({FileName}) by {UploadedBy}",
            entity.Id, fileName, uploadedBy);

        // Analyze the source asynchronously (don't wait)
        try
        {
            await AnalyzeInternalAsync(entity, fullPath);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze HMap source {SourceId} during upload", entity.Id);
            // Continue - analysis can be retried later
        }

        return MapToDto(entity);
    }

    public async Task<List<HmapSourceDto>> GetAllAsync()
    {
        var entities = await _dbContext.HmapSources
            .OrderByDescending(s => s.UploadedAt)
            .ToListAsync();

        return entities.Select(MapToDto).ToList();
    }

    public async Task<HmapSourceDto?> GetAsync(int id)
    {
        var entity = await _dbContext.HmapSources.FindAsync(id);
        return entity == null ? null : MapToDto(entity);
    }

    public async Task DeleteAsync(int id)
    {
        var entity = await _dbContext.HmapSources.FindAsync(id);
        if (entity == null)
        {
            throw new ArgumentException($"HMap source {id} not found");
        }

        // Check if source is in use by any public maps
        var inUse = await _dbContext.PublicMapHmapSources.AnyAsync(pms => pms.HmapSourceId == id);
        if (inUse)
        {
            throw new InvalidOperationException("Cannot delete HMap source that is in use by public maps");
        }

        // Delete file from disk
        var fullPath = Path.Combine(_gridStorage, entity.FilePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted HMap file {FilePath}", entity.FilePath);
        }

        _dbContext.HmapSources.Remove(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted HMap source {SourceId}", id);
    }

    public async Task<HmapSourceAnalysisDto> AnalyzeAsync(int sourceId)
    {
        var entity = await _dbContext.HmapSources.FindAsync(sourceId);
        if (entity == null)
        {
            throw new ArgumentException($"HMap source {sourceId} not found");
        }

        var fullPath = Path.Combine(_gridStorage, entity.FilePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"HMap file not found at {entity.FilePath}");
        }

        var analysis = await AnalyzeInternalAsync(entity, fullPath);
        await _dbContext.SaveChangesAsync();

        return analysis;
    }

    public async Task ReanalyzeAllAsync()
    {
        var sources = await _dbContext.HmapSources.ToListAsync();

        foreach (var source in sources)
        {
            try
            {
                var fullPath = Path.Combine(_gridStorage, source.FilePath);
                if (File.Exists(fullPath))
                {
                    await AnalyzeInternalAsync(source, fullPath);
                }
                else
                {
                    _logger.LogWarning("HMap file not found for source {SourceId}: {FilePath}",
                        source.Id, source.FilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze HMap source {SourceId}", source.Id);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync(int id)
    {
        return await _dbContext.HmapSources.AnyAsync(s => s.Id == id);
    }

    public async Task<string?> GetFilePathAsync(int id)
    {
        var entity = await _dbContext.HmapSources.FindAsync(id);
        if (entity == null)
            return null;

        return Path.Combine(_gridStorage, entity.FilePath);
    }

    public async Task<bool> IsSourceInUseAsync(int id)
    {
        return await _dbContext.PublicMapHmapSources.AnyAsync(pms => pms.HmapSourceId == id);
    }

    public async Task<List<string>> GetPublicMapsUsingSourceAsync(int id)
    {
        return await _dbContext.PublicMapHmapSources
            .Where(pms => pms.HmapSourceId == id)
            .Select(pms => pms.PublicMapId)
            .ToListAsync();
    }

    private async Task<HmapSourceAnalysisDto> AnalyzeInternalAsync(HmapSourceEntity entity, string fullPath)
    {
        _logger.LogInformation("Analyzing HMap source {SourceId}", entity.Id);

        await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        var reader = new HmapReader();
        var hmapData = reader.Read(fileStream);

        var grids = hmapData.Grids;

        if (grids.Count == 0)
        {
            entity.TotalGrids = 0;
            entity.SegmentCount = 0;
            entity.AnalyzedAt = DateTime.UtcNow;

            return new HmapSourceAnalysisDto
            {
                Id = entity.Id,
                TotalGrids = 0,
                SegmentCount = 0,
                Segments = new List<HmapSegmentInfo>()
            };
        }

        // Calculate overall bounds
        var minX = grids.Min(g => g.TileX);
        var maxX = grids.Max(g => g.TileX);
        var minY = grids.Min(g => g.TileY);
        var maxY = grids.Max(g => g.TileY);

        // Group by segment
        var segments = grids
            .GroupBy(g => g.SegmentId)
            .Select(sg => new HmapSegmentInfo
            {
                SegmentId = sg.Key,
                GridCount = sg.Count(),
                MinX = sg.Min(g => g.TileX),
                MaxX = sg.Max(g => g.TileX),
                MinY = sg.Min(g => g.TileY),
                MaxY = sg.Max(g => g.TileY)
            })
            .OrderByDescending(s => s.GridCount)
            .ToList();

        // Update entity
        entity.TotalGrids = grids.Count;
        entity.SegmentCount = segments.Count;
        entity.MinX = minX;
        entity.MaxX = maxX;
        entity.MinY = minY;
        entity.MaxY = maxY;
        entity.AnalyzedAt = DateTime.UtcNow;

        _logger.LogInformation("HMap source {SourceId} analysis complete: {GridCount} grids, {SegmentCount} segments, bounds ({MinX},{MinY}) to ({MaxX},{MaxY})",
            entity.Id, grids.Count, segments.Count, minX, minY, maxX, maxY);

        return new HmapSourceAnalysisDto
        {
            Id = entity.Id,
            TotalGrids = grids.Count,
            SegmentCount = segments.Count,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY,
            Segments = segments
        };
    }

    private static HmapSourceDto MapToDto(HmapSourceEntity entity)
    {
        return new HmapSourceDto
        {
            Id = entity.Id,
            Name = entity.Name,
            FileName = entity.FileName,
            FileSizeBytes = entity.FileSizeBytes,
            UploadedAt = entity.UploadedAt,
            UploadedBy = entity.UploadedBy,
            TotalGrids = entity.TotalGrids,
            SegmentCount = entity.SegmentCount,
            MinX = entity.MinX,
            MaxX = entity.MaxX,
            MinY = entity.MinY,
            MaxY = entity.MaxY,
            AnalyzedAt = entity.AnalyzedAt
        };
    }
}
