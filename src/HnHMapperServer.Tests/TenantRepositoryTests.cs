using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// TenantRepository.UpdateAsync must never write back the counters other writers own with atomic
/// UPDATEs (CurrentStorageMB: StorageQuotaService; LastActivityAt: TenantActivityService). It used
/// to call Tenants.Update() on the entity loaded at request start, which marks every column
/// modified — a superadmin rename overwrote the uploads' storage deltas made meanwhile.
/// Real SQLite, two contexts on one file.
/// </summary>
public class TenantRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly List<ApplicationDbContext> _contexts = new();

    private const string TenantId = "repo-tenant";
    private static readonly DateTime SeedActivity = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewActivity = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    public TenantRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-tenantrepo-test-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        var seed = NewContext();
        seed.Database.EnsureCreated();
        seed.Tenants.Add(new TenantEntity
        {
            Id = TenantId, Name = "Old name", StorageQuotaMB = 1024, CurrentStorageMB = 50,
            CreatedAt = DateTime.UtcNow, IsActive = true, LastActivityAt = SeedActivity
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
    }

    private ApplicationDbContext NewContext()
    {
        var context = new ApplicationDbContext(_options);
        _contexts.Add(context);
        return context;
    }

    private async Task ConcurrentWritersAsync()
    {
        var ctxB = NewContext();
        await new StorageQuotaService(ctxB, Mock.Of<ILogger<StorageQuotaService>>())
            .IncrementStorageUsageAsync(TenantId, 5);
        await ctxB.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == TenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastActivityAt, NewActivity));
    }

    private TenantEntity ReadFresh()
    {
        using var fresh = new ApplicationDbContext(_options);
        return fresh.Tenants.IgnoreQueryFilters().AsNoTracking().Single(t => t.Id == TenantId);
    }

    [Fact]
    public async Task UpdateAsync_NameChange_PreservesConcurrentStorageAndActivity()
    {
        var repoA = new TenantRepository(NewContext());
        var tenant = await repoA.GetByIdAsync(TenantId);
        Assert.NotNull(tenant);
        tenant.Name = "New name";
        tenant.StorageQuotaMB = 2048;

        await ConcurrentWritersAsync();
        await repoA.UpdateAsync(tenant);

        var row = ReadFresh();
        Assert.Equal("New name", row.Name);
        Assert.Equal(2048, row.StorageQuotaMB);
        Assert.Equal(55, row.CurrentStorageMB, precision: 9);
        Assert.Equal(NewActivity, row.LastActivityAt);
    }

    [Fact]
    public async Task UpdateAsync_DetachedEntity_DoesNotWriteCounters()
    {
        TenantEntity detached;
        using (var load = new ApplicationDbContext(_options))
        {
            detached = load.Tenants.IgnoreQueryFilters().AsNoTracking().Single(t => t.Id == TenantId);
        }
        detached.Name = "Detached rename";
        detached.CurrentStorageMB = 0; // a stale or bogus value must not reach the row
        detached.LastActivityAt = SeedActivity;

        await ConcurrentWritersAsync();
        await new TenantRepository(NewContext()).UpdateAsync(detached);

        var row = ReadFresh();
        Assert.Equal("Detached rename", row.Name);
        Assert.Equal(55, row.CurrentStorageMB, precision: 9);
        Assert.Equal(NewActivity, row.LastActivityAt);
    }
}
