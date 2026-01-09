using System.Diagnostics;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Background service that loads public map tiles into memory on startup.
/// </summary>
public class PublicTileCacheHostedService : IHostedService
{
    private readonly PublicTileCacheService _cache;
    private readonly ILogger<PublicTileCacheHostedService> _logger;

    public PublicTileCacheHostedService(
        PublicTileCacheService cache,
        ILogger<PublicTileCacheHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading public map tiles into memory...");

        var sw = Stopwatch.StartNew();
        await _cache.LoadAllTilesAsync(cancellationToken);
        sw.Stop();

        _logger.LogInformation(
            "Public tile cache ready: {Count} tiles, {Size:F1} MB loaded in {Time:F1}s",
            _cache.TileCount,
            _cache.MemoryUsageBytes / 1024.0 / 1024.0,
            sw.Elapsed.TotalSeconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
