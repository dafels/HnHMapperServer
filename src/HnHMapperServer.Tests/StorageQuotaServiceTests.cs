using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Storage quota accounting: one atomic delta UPDATE per charge (never a read-modify-write of a
/// possibly stale tracked tenant), the clamp at 0, quota checks that read the row, and the
/// authoritative reconciliation. Real SQLite (temp file, Pooling=False): ExecuteUpdate does not run
/// on the InMemory provider, and two DbContexts on one file play two concurrent request scopes.
/// </summary>
public class StorageQuotaServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _gridStorage;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly List<ApplicationDbContext> _contexts = new();

    private const string TenantId = "quota-tenant";
    private const double MiB = 1024.0 * 1024.0;

    public StorageQuotaServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-quota-test-{Guid.NewGuid():N}.db");
        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-quota-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        var seed = NewContext();
        seed.Database.EnsureCreated();
        seed.Tenants.Add(new TenantEntity
        {
            Id = TenantId, Name = TenantId, StorageQuotaMB = 1024, CurrentStorageMB = 100,
            CreatedAt = DateTime.UtcNow, IsActive = true
        });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }
        try { File.Delete(_dbPath); } catch (IOException) { }
        try { Directory.Delete(_gridStorage, recursive: true); }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException) { }
    }

    private ApplicationDbContext NewContext()
    {
        var context = new ApplicationDbContext(_options);
        _contexts.Add(context);
        return context;
    }

    private static StorageQuotaService Service(ApplicationDbContext db) =>
        new(db, Mock.Of<ILogger<StorageQuotaService>>());

    private double ReadUsage()
    {
        using var fresh = new ApplicationDbContext(_options);
        return fresh.Tenants.IgnoreQueryFilters().AsNoTracking().Single(t => t.Id == TenantId).CurrentStorageMB;
    }

    private void SetUsage(double mb, int? quotaMB = null)
    {
        using var db = new ApplicationDbContext(_options);
        var tenant = db.Tenants.IgnoreQueryFilters().Single(t => t.Id == TenantId);
        tenant.CurrentStorageMB = mb;
        if (quotaMB.HasValue) tenant.StorageQuotaMB = quotaMB.Value;
        db.SaveChanges();
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_RequestScopeStaleTrackedTenant_KeepsConcurrentDelta()
    {
        var ctxA = NewContext();
        var ctxB = NewContext();
        // Request A tracks the tenant (as token validation does) at 100.
        await ctxA.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId);

        await Service(ctxB).IncrementStorageUsageAsync(TenantId, 5);
        await Service(ctxA).IncrementStorageUsageAsync(TenantId, 1);

        // The old read-modify-write saved A's snapshot + 1 = 101 and lost B's +5.
        Assert.Equal(106, ReadUsage(), precision: 9);
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_AfterGetAllAsyncSnapshot_KeepsConcurrentDelta()
    {
        // ZoomTileRebuildService's shape: one scope lists every tenant at job start, then charges
        // rebuilt tiles for the whole run while uploads charge from other scopes.
        var ctxA = NewContext();
        var ctxB = NewContext();
        await new TenantRepository(ctxA).GetAllAsync();

        await Service(ctxB).IncrementStorageUsageAsync(TenantId, 5);
        await Service(ctxA).IncrementStorageUsageAsync(TenantId, 1);
        await Service(ctxA).IncrementStorageUsageAsync(TenantId, 1);

        Assert.Equal(107, ReadUsage(), precision: 9);
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_NegativeBeyondZero_ClampsToZero()
    {
        await Service(NewContext()).IncrementStorageUsageAsync(TenantId, -250);
        Assert.Equal(0, ReadUsage());

        await Service(NewContext()).DecrementStorageUsageAsync(TenantId, 3);
        Assert.Equal(0, ReadUsage());
    }

    [Fact]
    public async Task DecrementStorageUsageAsync_SubtractsAtomically()
    {
        await Service(NewContext()).DecrementStorageUsageAsync(TenantId, 40);
        Assert.Equal(60, ReadUsage(), precision: 9);
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_UnknownTenant_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(NewContext()).IncrementStorageUsageAsync("no-such-tenant", 1));
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_ZeroDelta_IsNoOp_EvenForUnknownTenant()
    {
        await Service(NewContext()).IncrementStorageUsageAsync("no-such-tenant", 0);
        await Service(NewContext()).IncrementStorageUsageAsync(TenantId, 0);
        Assert.Equal(100, ReadUsage());
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_DoesNotSaveCallerPendingChanges()
    {
        // Contract change: the old implementation called SaveChanges and flushed whatever the
        // caller had pending. Every caller now flushes its own changes first.
        var ctx = NewContext();
        var map = new MapInfoEntity { Name = "pending", TenantId = TenantId, CreatedAt = DateTime.UtcNow };
        ctx.Maps.Add(map);

        await Service(ctx).IncrementStorageUsageAsync(TenantId, 1);

        Assert.Equal(EntityState.Added, ctx.Entry(map).State);
        Assert.Equal(101, ReadUsage(), precision: 9);
    }

    [Fact]
    public async Task IncrementStorageUsageAsync_ConcurrentScopes_LoseNoDelta()
    {
        // 40 charges of 0.25 MB from 8 scopes at once: an UPDATE ... SET x = x + d per charge.
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var db = new ApplicationDbContext(_options);
            var service = Service(db);
            for (var n = 0; n < 5; n++)
            {
                await RetryOnBusyAsync(() => service.IncrementStorageUsageAsync(TenantId, 0.25));
            }
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(110, ReadUsage(), precision: 9);
    }

    private static async Task RetryOnBusyAsync(Func<Task> action)
    {
        // SQLite serializes writers; a concurrent writer can briefly report SQLITE_BUSY. A retry
        // is what a second request would do — the point here is that no delta is lost.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
        }
    }

    [Fact]
    public async Task CheckQuotaAsync_ReadsRowNotTrackedSnapshot()
    {
        SetUsage(0, quotaMB: 10);
        var ctxA = NewContext();
        var ctxB = NewContext();
        await ctxA.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId); // tracked at 0

        await Service(ctxB).IncrementStorageUsageAsync(TenantId, 9.5);

        Assert.False(await Service(ctxA).CheckQuotaAsync(TenantId, 1));
        Assert.True(await Service(ctxA).CheckQuotaAsync(TenantId, 0.5));
    }

    [Fact]
    public async Task CheckQuotaAsync_ZeroGrowth_AllowedAtQuota_BlockedOverQuota()
    {
        SetUsage(10, quotaMB: 10);
        Assert.True(await Service(NewContext()).CheckQuotaAsync(TenantId, 0));

        SetUsage(10.5, quotaMB: 10);
        Assert.False(await Service(NewContext()).CheckQuotaAsync(TenantId, 0));
    }

    [Fact]
    public async Task ReconcileStorageUsageAsync_ReportsFreshPrevious_SetsDiskTotal_SyncsTrackedEntry()
    {
        var ctxA = NewContext();
        var ctxB = NewContext();
        var tracked = await ctxA.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId); // 100
        await Service(ctxB).IncrementStorageUsageAsync(TenantId, 5);

        var tenantDir = Path.Combine(_gridStorage, "tenants", TenantId);
        WriteFile(Path.Combine(tenantDir, "grids", "g1.png"), 1000);
        WriteFile(Path.Combine(tenantDir, "1", "1", "0_0.png"), 2000);
        WriteFile(Path.Combine(tenantDir, "large", "1", "0", "0_0.webp"), 3000);

        var r = await Service(ctxA).ReconcileStorageUsageAsync(TenantId, _gridStorage);

        Assert.Equal(105, r.PreviousMB, precision: 9); // fresh, not the tracked 100
        Assert.Equal(6000 / MiB, r.CurrentMB, precision: 9);
        Assert.Equal(3, r.FileCount);
        Assert.Equal(1024, r.QuotaMB);
        Assert.Equal(105 - 6000 / MiB, r.DriftMB, precision: 9);
        Assert.Equal(6000 / MiB, ReadUsage(), precision: 9);

        // The tracked copy follows and stays Unchanged, so a later SaveChanges writes nothing back.
        Assert.Equal(6000 / MiB, tracked.CurrentStorageMB, precision: 9);
        Assert.Equal(EntityState.Unchanged, ctxA.Entry(tracked).State);
        Assert.True(File.Exists(Path.Combine(tenantDir, ".storage.json")));
    }

    [Fact]
    public async Task RecalculateStorageUsageAsync_ReturnsDiskTotal()
    {
        WriteFile(Path.Combine(_gridStorage, "tenants", TenantId, "grids", "g1.png"), 4096);
        var mb = await Service(NewContext()).RecalculateStorageUsageAsync(TenantId, _gridStorage);
        Assert.Equal(4096 / MiB, mb, precision: 9);
        Assert.Equal(4096 / MiB, ReadUsage(), precision: 9);
    }

    [Fact]
    public async Task ReconcileStorageUsageAsync_MissingTenantDir_SetsZero()
    {
        var r = await Service(NewContext()).ReconcileStorageUsageAsync(TenantId, _gridStorage);
        Assert.Equal(0, r.CurrentMB);
        Assert.Equal(0, r.FileCount);
        Assert.Equal(100, r.DriftMB, precision: 9);
        Assert.Equal(0, ReadUsage());
    }

    [Fact]
    public async Task ReconcileStorageUsageAsync_UnknownTenant_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(NewContext()).ReconcileStorageUsageAsync("no-such-tenant", _gridStorage));
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
