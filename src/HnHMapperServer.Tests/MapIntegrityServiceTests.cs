using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for the superadmin cross-tenant integrity scan (contested cells + placeholder rows).
/// Runs on a kept-open SQLite ":memory:" connection so the GroupBy/HAVING aggregate translates
/// through real SQL; the DbContext is built WITHOUT an IHttpContextAccessor (ambient tenant null,
/// exactly like a superadmin request) to prove every query carries IgnoreQueryFilters.
/// </summary>
public class MapIntegrityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly MapIntegrityService _service;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    public MapIntegrityServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Tenants.AddRange(
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantA, Name = "Tenant Alpha", StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            },
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantB, Name = "Tenant Beta", StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            });
        _db.SaveChanges();

        _service = new MapIntegrityService(_db, Mock.Of<ILogger<MapIntegrityService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void SeedMap(int mapId, string tenantId, string name)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = name, TenantId = tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    private void SeedGrid(string id, int mapId, string tenantId, int x, int y)
    {
        _db.Grids.Add(new GridDataEntity
        {
            Id = id, Map = mapId, CoordX = x, CoordY = y,
            NextUpdate = DateTime.UtcNow, TenantId = tenantId
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ScanAsync_FindsContestedCellsWithBboxAndOwners()
    {
        SeedMap(1, TenantA, "Alpha World");
        // Two contested cells (two owners each) + two unique cells
        SeedGrid("a1", 1, TenantA, 10, 10);
        SeedGrid("a2", 1, TenantA, 10, 10); // fights a1
        SeedGrid("b1", 1, TenantA, 15, 12);
        SeedGrid("b2", 1, TenantA, 15, 12); // fights b1
        SeedGrid("clean1", 1, TenantA, 0, 0);
        SeedGrid("clean2", 1, TenantA, 1, 0);

        var report = await _service.ScanAsync();

        var issue = Assert.Single(report.ContestedMaps);
        Assert.Equal(TenantA, issue.TenantId);
        Assert.Equal("Tenant Alpha", issue.TenantName);
        Assert.Equal(1, issue.MapId);
        Assert.Equal("Alpha World", issue.MapName);
        Assert.Equal(2, issue.ContestedCellCount);
        Assert.Equal(10, issue.MinX);
        Assert.Equal(15, issue.MaxX);
        Assert.Equal(10, issue.MinY);
        Assert.Equal(12, issue.MaxY);

        Assert.Equal(2, issue.SampleCells.Count);
        var cell = issue.SampleCells.Single(c => c.X == 10 && c.Y == 10);
        Assert.Equal(new[] { "a1", "a2" }.OrderBy(s => s), cell.GridIds.OrderBy(s => s));
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task ScanAsync_AttributesIssuesPerTenantAndMap()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedMap(2, TenantB, "Beta World");
        // Same coordinates contested in both tenants — must be two separate issues
        SeedGrid("a1", 1, TenantA, 5, 5);
        SeedGrid("a2", 1, TenantA, 5, 5);
        SeedGrid("b1", 2, TenantB, 5, 5);
        SeedGrid("b2", 2, TenantB, 5, 5);

        var report = await _service.ScanAsync();

        Assert.Equal(2, report.ContestedMaps.Count);
        Assert.Contains(report.ContestedMaps, i => i.TenantId == TenantA && i.MapId == 1);
        Assert.Contains(report.ContestedMaps, i => i.TenantId == TenantB && i.MapId == 2);
        // Owners never leak across tenants
        var alpha = report.ContestedMaps.Single(i => i.TenantId == TenantA);
        Assert.All(alpha.SampleCells.Single().GridIds, id => Assert.StartsWith("a", id));
    }

    [Fact]
    public async Task ScanAsync_ListsPlaceholderRowsPerTenant()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedMap(2, TenantB, "Beta World");
        SeedGrid("0", 1, TenantA, 18, 23);
        SeedGrid("0", 2, TenantB, 1, -4); // same id, different tenant — PK is (Id, TenantId)
        SeedGrid("real", 1, TenantA, 0, 0);

        var report = await _service.ScanAsync();

        Assert.Equal(2, report.PlaceholderRows.Count);
        var a = report.PlaceholderRows.Single(p => p.TenantId == TenantA);
        Assert.Equal("Tenant Alpha", a.TenantName);
        Assert.Equal(1, a.MapId);
        Assert.Equal(18, a.X);
        Assert.Equal(23, a.Y);
        Assert.False(report.IsClean);
        Assert.Empty(report.ContestedMaps); // placeholders alone are not contested cells
    }

    [Fact]
    public async Task ScanAsync_CleanDatabase_ReportsClean()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedGrid("g1", 1, TenantA, 0, 0);
        SeedGrid("g2", 1, TenantA, 1, 0);

        var report = await _service.ScanAsync();

        Assert.True(report.IsClean);
        Assert.Empty(report.ContestedMaps);
        Assert.Empty(report.PlaceholderRows);
        Assert.Equal(2, report.TotalGrids);
        Assert.Equal(2, report.TenantsScanned);
    }

    [Fact]
    public async Task ScanAsync_SampleCellsAreCapped_CountIsNot()
    {
        SeedMap(1, TenantA, "Alpha World");
        var total = MapIntegrityService.SampleCellCap + 3;
        for (var i = 0; i < total; i++)
        {
            SeedGrid($"x{i}", 1, TenantA, i, 0);
            SeedGrid($"y{i}", 1, TenantA, i, 0); // every cell contested
        }

        var report = await _service.ScanAsync();

        var issue = Assert.Single(report.ContestedMaps);
        Assert.Equal(total, issue.ContestedCellCount);
        Assert.Equal(MapIntegrityService.SampleCellCap, issue.SampleCells.Count);
    }
}
