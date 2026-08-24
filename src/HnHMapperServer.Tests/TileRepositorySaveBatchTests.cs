using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// TileRepository.SaveTilesBatchAsync hardening (2026-08-24 incident): the existence check
/// only filtered against rows already in the DB, so two tiles sharing a
/// (MapId, Zoom, CoordX, CoordY) key inside ONE batch both passed it and SaveChanges died
/// on the Tiles unique index — and a concurrent writer inserting between the check and the
/// save produced the same failure. Real SQLite (unique indexes must actually fire).
/// </summary>
public class TileRepositorySaveBatchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TileRepository _repository;
    private readonly ConcurrentTileInsertInterceptor _interceptor;

    private const string TestTenantId = "default-tenant-1";

    public TileRepositorySaveBatchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        _interceptor = new ConcurrentTileInsertInterceptor(_connection);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;
        _dbContext = new ApplicationDbContext(options, mockHttpContextAccessor.Object);
        _dbContext.Database.EnsureCreated();

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = TestTenantId,
            Name = TestTenantId,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        _dbContext.SaveChanges();

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        _repository = new TileRepository(_dbContext, mockTenantContext.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static TileData Tile(int mapId, int x, int y, string file, int zoom = 0) => new()
    {
        MapId = mapId,
        Coord = new Coord(x, y),
        Zoom = zoom,
        File = file,
        Cache = 1,
        TenantId = TestTenantId,
        FileSizeBytes = 10
    };

    [Fact]
    public async Task SaveTilesBatchAsync_DuplicateKeyWithinBatch_SavesOneRowFirstWins()
    {
        // Pre-fix: both duplicates passed the DB-only existence check and SaveChanges threw
        // 'UNIQUE constraint failed: Tiles.MapId, Tiles.Zoom, Tiles.CoordX, Tiles.CoordY'.
        await _repository.SaveTilesBatchAsync(new[]
        {
            Tile(7, 0, 0, "first.png"),
            Tile(7, 0, 0, "second.png"),
            Tile(7, 1, 0, "other.png")
        });

        var rows = await _dbContext.Tiles.AsNoTracking()
            .Where(t => t.MapId == 7).OrderBy(t => t.CoordX).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("first.png", rows[0].File);
        Assert.Equal("other.png", rows[1].File);
    }

    [Fact]
    public async Task SaveTilesBatchAsync_RowAlreadyInDb_SkippedWithoutTouchingIt()
    {
        await _repository.SaveTilesBatchAsync(new[] { Tile(7, 0, 0, "original.png") });

        await _repository.SaveTilesBatchAsync(new[]
        {
            Tile(7, 0, 0, "would-overwrite.png"),
            Tile(7, 1, 0, "new.png")
        });

        var rows = await _dbContext.Tiles.AsNoTracking()
            .Where(t => t.MapId == 7).OrderBy(t => t.CoordX).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("original.png", rows[0].File);
        Assert.Equal("new.png", rows[1].File);
    }

    [Fact]
    public async Task SaveTilesBatchAsync_CheckThenInsertRace_RetriesOnceAndSavesSurvivors()
    {
        // The interceptor plays the concurrent writer: after the existence check has run
        // (which saw nothing), it inserts the (7,0,0,z0) row via raw SQL on the same
        // connection just before EF executes its own INSERTs. Pre-fix that failed the whole
        // batch and left the context poisoned; now the batch detaches, re-checks and retries
        // once with the survivors.
        _interceptor.Armed = true;

        await _repository.SaveTilesBatchAsync(new[]
        {
            Tile(7, 0, 0, "loser.png"),
            Tile(7, 1, 0, "survivor.png")
        });

        Assert.True(_interceptor.Fired);
        var rows = await _dbContext.Tiles.AsNoTracking()
            .Where(t => t.MapId == 7).OrderBy(t => t.CoordX).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("winner.png", rows[0].File);     // the concurrent writer's row survived
        Assert.Equal("survivor.png", rows[1].File);   // the non-conflicting tile still landed

        // And the context is not poisoned: an unrelated follow-up save works
        _interceptor.Armed = false;
        await _repository.SaveTilesBatchAsync(new[] { Tile(7, 2, 0, "later.png") });
        Assert.Equal(3, await _dbContext.Tiles.AsNoTracking().CountAsync(t => t.MapId == 7));
    }

    /// <summary>
    /// Simulates a concurrent writer (live gridUpdate, background zoom rebuild): the first
    /// armed SaveChanges that is about to insert Tiles rows gets a raw-SQL insert of the
    /// contested key slipped in ahead of it, on the same connection (":memory:" is
    /// per-connection). SavingChanges fires before EF opens its implicit transaction, so
    /// the raw insert commits independently.
    /// </summary>
    private sealed class ConcurrentTileInsertInterceptor : SaveChangesInterceptor
    {
        private readonly SqliteConnection _connection;
        public bool Armed { get; set; }
        public bool Fired { get; private set; }

        public ConcurrentTileInsertInterceptor(SqliteConnection connection) => _connection = connection;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            InsertContestedRowOnce(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            InsertContestedRowOnce(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void InsertContestedRowOnce(DbContextEventData eventData)
        {
            if (!Armed || Fired || eventData.Context is not ApplicationDbContext ctx)
                return;
            if (!ctx.ChangeTracker.Entries<TileDataEntity>().Any(e => e.State == EntityState.Added))
                return;

            Fired = true;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO Tiles (MapId, CoordX, CoordY, Zoom, File, Cache, TenantId, FileSizeBytes) " +
                $"VALUES (7, 0, 0, 0, 'winner.png', 1, '{TestTenantId}', 10)";
            cmd.ExecuteNonQuery();
        }
    }
}
