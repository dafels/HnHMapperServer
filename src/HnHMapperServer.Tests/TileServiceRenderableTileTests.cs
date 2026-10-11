using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// <see cref="TileService.HasRenderableTileAsync"/>: GridUpload's no-updates guard (AllowGridUpdates
/// = false) protects only a tile that is actually drawn. A row whose file is gone — a merge deleted
/// it, lost on disk — used to block the one upload that could heal the cell.
/// </summary>
public class TileServiceRenderableTileTests : IDisposable
{
    private const string TenantId = "renderable-tenant";
    private const int MapId = 3;

    private readonly ApplicationDbContext _db;
    private readonly TileService _tileService;
    private readonly string _gridStorage;

    public TileServiceRenderableTileTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantId;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            accessor.Object);

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TenantId);
        _tileService = new TileService(
            new TileRepository(_db, tenantContext.Object),
            new GridRepository(_db, tenantContext.Object),
            Mock.Of<IUpdateNotificationService>(),
            Mock.Of<IStorageQuotaService>(),
            Mock.Of<ILogger<TileService>>(),
            _db);

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-renderable-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    private void SeedRow(int x, string file)
    {
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = MapId, CoordX = x, CoordY = 0, Zoom = 0, File = file, Cache = 1, TenantId = TenantId
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task HasRenderableTile_OnlyForARowWhoseFileExists()
    {
        var present = Path.Combine("tenants", TenantId, "grids", "present.png");
        Directory.CreateDirectory(Path.Combine(_gridStorage, "tenants", TenantId, "grids"));
        File.WriteAllBytes(Path.Combine(_gridStorage, present), new byte[10]);

        SeedRow(1, present);
        SeedRow(2, Path.Combine("tenants", TenantId, "grids", "gone.png"));
        SeedRow(3, ""); // wipe tombstone

        Assert.True(await _tileService.HasRenderableTileAsync(MapId, new Coord(1, 0), _gridStorage));
        Assert.False(await _tileService.HasRenderableTileAsync(MapId, new Coord(2, 0), _gridStorage));
        Assert.False(await _tileService.HasRenderableTileAsync(MapId, new Coord(3, 0), _gridStorage));
        Assert.False(await _tileService.HasRenderableTileAsync(MapId, new Coord(4, 0), _gridStorage)); // no row
    }
}
