using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Integration tests for the superadmin "delete all map data" operation.
///
/// These run against real SQLite (not the in-memory provider): the service leans on
/// ExecuteDelete, which the in-memory provider does not implement at all, and the
/// delete ordering only matters because SQLite actually enforces the foreign keys.
/// </summary>
public class TenantDataPurgeServiceTests : IDisposable
{
    private const string TargetTenant = "purge-me-1";
    private const string OtherTenant = "keep-me-2";
    private const string UserId = "user-1";

    private readonly string _dbPath;
    private readonly string _gridStorage;
    private readonly ApplicationDbContext _db;
    private readonly TenantDataPurgeService _service;

    public TenantDataPurgeServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-purge-test-{Guid.NewGuid():N}.db");
        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-purge-storage-{Guid.NewGuid():N}");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        // No IHttpContextAccessor: the ambient tenant is null, exactly like a superadmin
        // request purging someone else's tenant. Everything must work through explicit ids.
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        Seed();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        _service = new TenantDataPurgeService(
            _db,
            new StorageQuotaService(_db, Mock.Of<ILogger<StorageQuotaService>>()),
            new TenantFilePathService(),
            configuration,
            Mock.Of<ILogger<TenantDataPurgeService>>());
    }

    [Fact]
    public async Task PurgeAsync_RemovesAllMapDataForTheTargetTenant()
    {
        var result = await _service.PurgeAsync(TargetTenant);

        Assert.Equal(1, result.Maps);
        Assert.Equal(1, result.Grids);
        Assert.Equal(1, result.Tiles);
        Assert.Equal(1, result.Markers);
        Assert.Equal(1, result.CustomMarkers);
        Assert.Equal(1, result.Roads);
        Assert.Equal(1, result.Pings);
        Assert.Equal(2, result.Overlays); // overlay data + overlay offset
        Assert.Equal(1, result.DirtyZoomTiles);
        Assert.Equal(1, result.Timers);
        Assert.Equal(1, result.TimerHistory);
        Assert.Equal(1, result.Notifications);

        Assert.Empty(await _db.Maps.IgnoreQueryFilters().Where(m => m.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Grids.IgnoreQueryFilters().Where(g => g.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Tiles.IgnoreQueryFilters().Where(t => t.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Markers.IgnoreQueryFilters().Where(m => m.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.CustomMarkers.IgnoreQueryFilters().Where(c => c.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Roads.IgnoreQueryFilters().Where(r => r.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Pings.IgnoreQueryFilters().Where(p => p.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.OverlayData.IgnoreQueryFilters().Where(o => o.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.OverlayOffsets.IgnoreQueryFilters().Where(o => o.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.DirtyZoomTiles.IgnoreQueryFilters().Where(d => d.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Timers.IgnoreQueryFilters().Where(t => t.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.TimerHistory.IgnoreQueryFilters().Where(t => t.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.Notifications.IgnoreQueryFilters().Where(n => n.TenantId == TargetTenant).ToListAsync());

        // TimerWarnings carry no TenantId; they must go with their timer via cascade,
        // leaving no orphans and no collateral damage to the other tenant's warning.
        var remainingWarnings = await _db.TimerWarnings.ToListAsync();
        var survivingTimerIds = await _db.Timers.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();

        Assert.Single(remainingWarnings);
        Assert.All(remainingWarnings, w => Assert.Contains(w.TimerId, survivingTimerIds));
    }

    [Fact]
    public async Task PurgeAsync_KeepsUsersTokensInvitationsAndPanels()
    {
        await _service.PurgeAsync(TargetTenant);

        Assert.NotNull(await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == TargetTenant));
        Assert.Single(await _db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.TenantId == TargetTenant).ToListAsync());
        Assert.Single(await _db.TenantPermissions.ToListAsync());
        Assert.Single(await _db.Tokens.IgnoreQueryFilters().Where(t => t.TenantId == TargetTenant).ToListAsync());
        Assert.Single(await _db.TenantInvitations.Where(i => i.TenantId == TargetTenant).ToListAsync());
        Assert.Single(await _db.FoodPanels.IgnoreQueryFilters().Where(p => p.TenantId == TargetTenant).ToListAsync());
        Assert.NotNull(await _db.Users.FirstOrDefaultAsync(u => u.Id == UserId));
    }

    [Fact]
    public async Task PurgeAsync_KeepsConfigButDropsTheDanglingMainMapPointer()
    {
        await _service.PurgeAsync(TargetTenant);

        var config = await _db.Config.IgnoreQueryFilters()
            .Where(c => c.TenantId == TargetTenant)
            .ToListAsync();

        Assert.Single(config);
        Assert.Equal("title", config[0].Key);
    }

    [Fact]
    public async Task PurgeAsync_LeavesOtherTenantsUntouched()
    {
        await _service.PurgeAsync(TargetTenant);

        Assert.Single(await _db.Maps.IgnoreQueryFilters().Where(m => m.TenantId == OtherTenant).ToListAsync());
        Assert.Single(await _db.Grids.IgnoreQueryFilters().Where(g => g.TenantId == OtherTenant).ToListAsync());
        Assert.Single(await _db.Tiles.IgnoreQueryFilters().Where(t => t.TenantId == OtherTenant).ToListAsync());
        Assert.Single(await _db.Markers.IgnoreQueryFilters().Where(m => m.TenantId == OtherTenant).ToListAsync());
        Assert.True(Directory.Exists(Path.Combine(_gridStorage, "tenants", OtherTenant)));
    }

    [Fact]
    public async Task PurgeAsync_DeletesTenantFilesAndReportsSpaceFreed()
    {
        var result = await _service.PurgeAsync(TargetTenant);

        // 3 tile/grid pngs + 1 preview.
        Assert.Equal(4, result.FilesDeleted);
        Assert.True(result.BytesFreed > 0, "expected the purge to report reclaimed bytes");
        Assert.Empty(result.Warnings);

        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "tenants", TargetTenant, "1")));
        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "previews", TargetTenant)));

        // The skeleton is recreated so a client can upload again immediately.
        Assert.True(Directory.Exists(Path.Combine(_gridStorage, "tenants", TargetTenant, "grids")));
    }

    [Fact]
    public async Task PurgeAsync_ResetsStorageUsageToZero()
    {
        await _service.PurgeAsync(TargetTenant);

        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == TargetTenant);

        // Only the freshly written .storage.json remains under the tenant directory.
        Assert.True(tenant.CurrentStorageMB < 0.01, $"expected ~0 MB, got {tenant.CurrentStorageMB}");
        Assert.Equal(1024, tenant.StorageQuotaMB);
    }

    [Fact]
    public async Task PurgeAsync_KeepsTheCookbook()
    {
        await _service.PurgeAsync(TargetTenant);

        // Foods and their variations hold player contributions no re-import can restore.
        Assert.Single(await _db.Foods.IgnoreQueryFilters().Where(f => f.TenantId == TargetTenant).ToListAsync());
        Assert.Single(await _db.FoodVariants.IgnoreQueryFilters().Where(v => v.TenantId == TargetTenant).ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_RemovesPublicMapReferencesToTheWipedMaps()
    {
        var result = await _service.PurgeAsync(TargetTenant);

        Assert.Equal(1, result.PublicMapSources);
        Assert.Empty(await _db.PublicMapSources.Where(s => s.TenantId == TargetTenant).ToListAsync());
        Assert.Empty(await _db.PublicMapSourceAlignments.Where(a => a.SourceTenantId == TargetTenant).ToListAsync());

        // The public map itself, and sources from other tenants, survive.
        Assert.Single(await _db.PublicMaps.ToListAsync());
        Assert.Single(await _db.PublicMapSources.Where(s => s.TenantId == OtherTenant).ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_ReportsDeletedMapIdsForCacheInvalidation()
    {
        var result = await _service.PurgeAsync(TargetTenant);

        Assert.Equal(new[] { 1 }, result.DeletedMapIds);
    }

    [Fact]
    public async Task PurgeAsync_ThrowsForUnknownTenant()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PurgeAsync("no-such-tenant"));
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;

        _db.Users.Add(new ApplicationUser
        {
            Id = UserId,
            UserName = "mapper",
            NormalizedUserName = "MAPPER"
        });

        foreach (var tenantId in new[] { TargetTenant, OtherTenant })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Name = tenantId,
                StorageQuotaMB = 1024,
                CurrentStorageMB = 250,
                CreatedAt = now,
                IsActive = true
            });
        }

        _db.SaveChanges();

        SeedTenantContent(TargetTenant, mapId: 1, now);
        SeedTenantContent(OtherTenant, mapId: 2, now);

        // Data that must survive the purge.
        var tenantUser = new TenantUserEntity
        {
            TenantId = TargetTenant,
            UserId = UserId,
            Role = TenantRole.TenantAdmin,
            JoinedAt = now
        };
        _db.TenantUsers.Add(tenantUser);
        _db.SaveChanges();

        _db.TenantPermissions.Add(new TenantPermissionEntity
        {
            TenantUserId = tenantUser.Id,
            Permission = Permission.Upload
        });

        _db.Tokens.Add(new TokenEntity
        {
            Id = Guid.NewGuid().ToString(),
            DisplayToken = $"{TargetTenant}_secret",
            TokenHash = "hash",
            TenantId = TargetTenant,
            UserId = UserId,
            Name = "client",
            CreatedAt = now
        });

        _db.TenantInvitations.Add(new TenantInvitationEntity
        {
            TenantId = TargetTenant,
            InviteCode = "INVITE1",
            CreatedBy = UserId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            Status = "Active"
        });

        _db.FoodPanels.Add(new FoodPanelEntity
        {
            TenantId = TargetTenant,
            UserId = UserId,
            Name = "Favorites",
            IsFavorites = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        // Public map wired to both tenants.
        _db.PublicMaps.Add(new PublicMapEntity
        {
            Id = "public-1",
            Name = "Public",
            CreatedAt = now,
            CreatedBy = UserId
        });
        _db.SaveChanges();

        foreach (var (tenantId, mapId) in new[] { (TargetTenant, 1), (OtherTenant, 2) })
        {
            _db.PublicMapSources.Add(new PublicMapSourceEntity
            {
                PublicMapId = "public-1",
                TenantId = tenantId,
                MapId = mapId,
                AddedAt = now,
                AddedBy = UserId
            });
        }

        _db.PublicMapSourceAlignments.Add(new PublicMapSourceAlignmentEntity
        {
            PublicMapId = "public-1",
            SourceType = "Tenant",
            SourceTenantId = TargetTenant,
            SourceMapId = 1,
            ComponentIndex = 0,
            UnifiedOffsetX = 0,
            UnifiedOffsetY = 0,
            MatchCountToComponent = 0,
            AlignmentConfidence = 1,
            IsStandalone = true,
            ComputedAt = now
        });

        _db.SaveChanges();
    }

    private void SeedTenantContent(string tenantId, int mapId, DateTime now)
    {
        var gridId = $"grid-{tenantId}";

        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"map-{mapId}", TenantId = tenantId, CreatedAt = now });
        _db.SaveChanges();

        _db.Grids.Add(new GridDataEntity
        {
            Id = gridId,
            CoordX = 0,
            CoordY = 0,
            NextUpdate = now,
            Map = mapId,
            TenantId = tenantId
        });

        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            Zoom = 0,
            File = Path.Combine("tenants", tenantId, mapId.ToString(), "0", "0_0.png"),
            Cache = 0,
            TenantId = tenantId,
            FileSizeBytes = 64
        });

        _db.SaveChanges();

        _db.Markers.Add(new MarkerEntity
        {
            Key = $"key-{tenantId}",
            Name = "marker",
            GridId = gridId,
            Image = "gfx/marker",
            TenantId = tenantId
        });

        _db.CustomMarkers.Add(new CustomMarkerEntity
        {
            MapId = mapId,
            GridId = gridId,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "custom",
            Icon = "gfx/icon",
            CreatedBy = UserId,
            PlacedAt = now,
            UpdatedAt = now,
            TenantId = tenantId
        });

        _db.Roads.Add(new RoadEntity
        {
            MapId = mapId,
            Name = "road",
            Waypoints = "[]",
            CreatedBy = UserId,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = tenantId
        });

        _db.Pings.Add(new PingEntity
        {
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            X = 1,
            Y = 1,
            CreatedBy = UserId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(1),
            TenantId = tenantId
        });

        _db.OverlayData.Add(new OverlayDataEntity
        {
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            OverlayType = "cave",
            Data = new byte[] { 1, 2, 3 },
            TenantId = tenantId,
            UpdatedAt = now
        });

        _db.OverlayOffsets.Add(new OverlayOffsetEntity
        {
            TenantId = tenantId,
            CurrentMapId = mapId,
            OverlayMapId = mapId,
            OffsetX = 0,
            OffsetY = 0,
            UpdatedAt = now
        });

        _db.DirtyZoomTiles.Add(new DirtyZoomTileEntity
        {
            TenantId = tenantId,
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            Zoom = 1,
            CreatedAt = now
        });

        _db.Notifications.Add(new NotificationEntity
        {
            TenantId = tenantId,
            UserId = null,
            Type = "MapEvent",
            Title = "hi",
            Message = "hi",
            CreatedAt = now
        });

        _db.Config.Add(new ConfigEntity { Key = "title", Value = "My map", TenantId = tenantId });
        _db.Config.Add(new ConfigEntity { Key = "mainMapId", Value = mapId.ToString(), TenantId = tenantId });

        _db.SaveChanges();

        var marker = _db.Markers.IgnoreQueryFilters().First(m => m.TenantId == tenantId);

        var timer = new TimerEntity
        {
            TenantId = tenantId,
            UserId = UserId,
            Type = "Marker",
            MarkerId = marker.Id,
            Title = "harvest",
            ReadyAt = now.AddHours(1),
            CreatedAt = now
        };
        _db.Timers.Add(timer);
        _db.SaveChanges();

        _db.TimerWarnings.Add(new TimerWarningEntity
        {
            TimerId = timer.Id,
            WarningMinutes = 5,
            SentAt = now
        });

        _db.TimerHistory.Add(new TimerHistoryEntity
        {
            TimerId = timer.Id,
            TenantId = tenantId,
            CompletedAt = now,
            Type = "Marker",
            Title = "harvest"
        });

        // Cookbook data must survive the purge (player contributions, not map data).
        var food = new FoodEntity
        {
            TenantId = tenantId,
            Name = "Roast Meat",
            ResourceName = "gfx/invobjs/roastmeat",
            Energy = 200,
            Hunger = 1,
            ImportedAt = now,
            ContributedBy = UserId
        };
        _db.Foods.Add(food);
        _db.SaveChanges();

        _db.FoodVariants.Add(new FoodVariantEntity
        {
            TenantId = tenantId,
            FoodId = food.Id,
            IngredientSignature = "Raw Meat:100",
            Energy = 200,
            Hunger = 1,
            TimesSeen = 1,
            Contributors = new List<string> { UserId }
        });

        _db.SaveChanges();

        // Files on disk: a grid png, two zoom tiles, and a map preview.
        WriteFile(Path.Combine(_gridStorage, "tenants", tenantId, "grids", $"{gridId}.png"), 512);
        WriteFile(Path.Combine(_gridStorage, "tenants", tenantId, mapId.ToString(), "0", "0_0.png"), 1024);
        WriteFile(Path.Combine(_gridStorage, "tenants", tenantId, "large", mapId.ToString(), "0", "0_0.webp"), 2048);
        WriteFile(Path.Combine(_gridStorage, "previews", tenantId, "preview.png"), 256);
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { File.Delete(_dbPath); } catch (IOException) { }
        try { Directory.Delete(_gridStorage, recursive: true); } catch (Exception e) when (e is IOException or DirectoryNotFoundException) { }
    }
}
