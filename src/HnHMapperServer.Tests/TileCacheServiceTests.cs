using System.Collections.Concurrent;
using System.Data.Common;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// TileCacheService serves /map/updates' tile tokens: since &gt; 0 (every reconnect) straight from the
/// database, since &lt;= 0 (a page load) from a per-tenant snapshot. It used to cache one list per
/// tenant for both, dropped by every gridUpload and ZoomQ batch, so most reconnects reloaded the whole
/// tenant (~22k rows, under one process-wide lock) to answer a 0-6 row delta.
///
/// Real SQLite in a temp file (Pooling=False: every scope opens its own connection to the same
/// database, and the file can be deleted). The DbContexts have no HttpContext, so the global tenant
/// filter reads "no tenant", and the ambient tenant accessor points at tenant B: only an explicit
/// TenantId predicate plus IgnoreQueryFilters returns tenant A's rows.
/// </summary>
public class TileCacheServiceTests : IDisposable
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";

    private readonly string _dbPath;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly CommandCapture _commands = new();
    private readonly ServiceProvider _provider;

    public TileCacheServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-tilecache-test-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .AddInterceptors(_commands)
            .Options;

        using (var db = new ApplicationDbContext(_options))
        {
            db.Database.EnsureCreated();
            foreach (var id in new[] { A, B })
            {
                db.Tenants.Add(new TenantEntity
                {
                    Id = id, Name = id, StorageQuotaMB = 1024, CreatedAt = DateTime.UtcNow, IsActive = true
                });
            }
            db.SaveChanges();
        }

        AddTile(A, mapId: 1, x: 0, y: 0, zoom: 0, cache: 100);
        AddTile(A, mapId: 1, x: 1, y: 0, zoom: 0, cache: 200);
        AddTile(A, mapId: 1, x: 2, y: 0, zoom: 0, cache: -1);   // wipe tombstone
        AddTile(B, mapId: 2, x: 0, y: 0, zoom: 0, cache: 150);

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(t => t.GetCurrentTenantId()).Returns(B);

        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(_options));
        services.AddScoped<ITileRepository>(sp =>
            new TileRepository(sp.GetRequiredService<ApplicationDbContext>(), tenantContext.Object));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private void AddTile(string tenantId, int mapId, int x, int y, int zoom, long cache)
    {
        using var db = new ApplicationDbContext(_options);
        db.Tiles.Add(new TileDataEntity
        {
            TenantId = tenantId, MapId = mapId, CoordX = x, CoordY = y, Zoom = zoom,
            File = $"tenants/{tenantId}/grids/{x}_{y}.png", Cache = cache, FileSizeBytes = 1
        });
        db.SaveChanges();
    }

    private CountingTileCacheService CreateService() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<TileCacheService>>());

    private static List<(int M, int X, int Y, int Z, long T)> Tokens(IEnumerable<TileCacheDto> tiles) =>
        tiles.Select(t => (t.M, t.X, t.Y, t.Z, t.T)).OrderBy(t => t.T).ThenBy(t => t.X).ToList();

    /// <summary>
    /// Counts snapshot loads per tenant; optionally holds a load (after it read the database) until
    /// released, or fails the next one.
    /// </summary>
    private sealed class CountingTileCacheService : TileCacheService
    {
        private readonly ConcurrentDictionary<string, int> _loads = new();

        public CountingTileCacheService(IServiceScopeFactory scopeFactory, ILogger<TileCacheService> logger)
            : base(scopeFactory, logger)
        {
        }

        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNext { get; set; }

        public int Loads(string tenantId) => _loads.GetValueOrDefault(tenantId);

        protected override async Task<IReadOnlyList<TileCacheDto>> LoadSnapshotTilesAsync(string tenantId)
        {
            _loads.AddOrUpdate(tenantId, 1, (_, n) => n + 1);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("database is locked");
            }

            var tiles = await base.LoadSnapshotTilesAsync(tenantId);
            Loaded.TrySetResult();
            if (Gate != null)
            {
                await Gate.Task;
            }
            return tiles;
        }
    }

    /// <summary>Keeps the SQL + parameters of the last Tiles query, for EXPLAIN QUERY PLAN.</summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public (string Sql, List<(string Name, object? Value)> Parameters)? LastTilesQuery { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Tiles\""))
            {
                LastTilesQuery = (command.CommandText,
                    command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList());
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task SinceZero_ReturnsTheWholeTenant_IncludingTombstones_AndNoOtherTenant()
    {
        var tiles = await CreateService().GetTilesSinceAsync(A, 0);

        Assert.Equal(
            new List<(int, int, int, int, long)> { (1, 2, 0, 0, -1), (1, 0, 0, 0, 100), (1, 1, 0, 0, 200) },
            Tokens(tiles));
    }

    [Fact]
    public async Task SincePositive_ReturnsStrictlyNewerRows()
    {
        var service = CreateService();

        Assert.Equal(new List<(int, int, int, int, long)> { (1, 1, 0, 0, 200) }, Tokens(await service.GetTilesSinceAsync(A, 100)));
        Assert.Empty(await service.GetTilesSinceAsync(A, 200));
        Assert.Equal(150, Assert.Single(await service.GetTilesSinceAsync(B, 1)).T);
    }

    [Fact]
    public async Task SinceZero_IsCachedUntilInvalidated()
    {
        var service = CreateService();
        Assert.Equal(3, (await service.GetTilesSinceAsync(A, 0)).Count);

        AddTile(A, mapId: 1, x: 3, y: 0, zoom: 0, cache: 300);
        Assert.Equal(3, (await service.GetTilesSinceAsync(A, 0)).Count);
        Assert.Equal(1, service.Loads(A));

        await service.InvalidateCacheAsync(A);

        Assert.Equal(4, (await service.GetTilesSinceAsync(A, 0)).Count);
        Assert.Equal(2, service.Loads(A));
    }

    [Fact]
    public async Task SincePositive_ReadsTheDatabase_AndNeverReloadsTheSnapshot()
    {
        // The churn regression: a reconnect delta after an upload must not rebuild the whole list
        var service = CreateService();
        await service.GetTilesSinceAsync(A, 0);
        AddTile(A, mapId: 1, x: 3, y: 0, zoom: 0, cache: 300);
        await service.InvalidateCacheAsync(A); // what the gridUpload does

        var delta = await service.GetTilesSinceAsync(A, 250);

        Assert.Equal(300, Assert.Single(delta).T);
        Assert.Equal(1, service.Loads(A));
    }

    [Fact]
    public async Task SincePositive_OnAColdTenant_DoesNotLoadASnapshot()
    {
        var service = CreateService();

        await service.GetTilesSinceAsync(A, 100);

        Assert.Equal(0, service.Loads(A));
        Assert.Empty(service.GetCacheStats(A));
    }

    [Fact]
    public async Task ConcurrentSinceZeroReads_ShareOneLoad()
    {
        var service = CreateService();
        service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reads = Enumerable.Range(0, 20).Select(_ => service.GetTilesSinceAsync(A, 0)).ToList();
        await service.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Gate.SetResult();
        var results = await Task.WhenAll(reads);

        Assert.Equal(1, service.Loads(A));
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(3, results[0].Count);
    }

    [Fact]
    public async Task FailedLoad_IsNotCached()
    {
        var service = CreateService();
        service.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTilesSinceAsync(A, 0));
        var tiles = await service.GetTilesSinceAsync(A, 0);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(2, service.Loads(A));
    }

    [Fact]
    public async Task InvalidationDuringAnInFlightLoad_DoesNotKeepTheStaleSnapshot()
    {
        var service = CreateService();
        service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = service.GetTilesSinceAsync(A, 0);
        await service.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); // it has read the database
        AddTile(A, mapId: 1, x: 3, y: 0, zoom: 0, cache: 300);
        await service.InvalidateCacheAsync(A);
        service.Gate.SetResult();

        Assert.Equal(3, (await inFlight).Count);                       // its caller gets what it read
        service.Gate = null;
        Assert.Equal(4, (await service.GetTilesSinceAsync(A, 0)).Count); // nobody else does
        Assert.Equal(2, service.Loads(A));
    }

    [Fact]
    public async Task Invalidate_IsTenantScoped_AndNullClearsAll()
    {
        var service = CreateService();
        await service.GetTilesSinceAsync(A, 0);
        await service.GetTilesSinceAsync(B, 0);

        await service.InvalidateCacheAsync(A);
        await service.GetTilesSinceAsync(A, 0);
        await service.GetTilesSinceAsync(B, 0);
        Assert.Equal((2, 1), (service.Loads(A), service.Loads(B)));
        Assert.Equal(2, service.GetCacheStats().Count);

        await service.InvalidateCacheAsync(null);
        Assert.Empty(service.GetCacheStats());
        await service.GetTilesSinceAsync(A, 0);
        await service.GetTilesSinceAsync(B, 0);
        Assert.Equal((3, 2), (service.Loads(A), service.Loads(B)));
    }

    [Fact]
    public async Task DeltaQuery_UsesTheTenantCacheIndex()
    {
        await CreateService().GetTilesSinceAsync(A, 150);
        var (sql, parameters) = _commands.LastTilesQuery ?? throw new Xunit.Sdk.XunitException("no Tiles query captured");

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            explain.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, line => line.Contains("IX_Tiles_TenantId_Cache") && line.Contains("Cache>?"));
    }
}
