using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// NotificationService behavior added for live delivery: SSE broadcast on create,
/// code-enforced length caps (SQLite ignores HasMaxLength), and the tenant-filter
/// bypass in DeleteExpiredAsync (background services have no HttpContext, so the
/// ambient filter would resolve to TenantId == NULL and delete nothing).
/// </summary>
public class NotificationServiceTests : IDisposable
{
    private const string TenantA = "notif-a";
    private const string TenantB = "notif-b";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly UpdateNotificationService _updateSvc;
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-notif-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        foreach (var tenantId in new[] { TenantA, TenantB })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Name = tenantId,
                StorageQuotaMB = 1024,
                CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });
        }
        _db.SaveChanges();

        _updateSvc = new UpdateNotificationService();
        _service = new NotificationService(
            _db,
            Mock.Of<ILogger<NotificationService>>(),
            Mock.Of<IServiceScopeFactory>(),
            _updateSvc);
    }

    [Fact]
    public async Task CreateAsync_BroadcastsCreatedEvent()
    {
        var created = _updateSvc.SubscribeToNotificationCreated().Reader;

        var dto = await _service.CreateAsync(new CreateNotificationDto
        {
            TenantId = TenantA,
            UserId = null,
            Type = "CookbookFoodAdded",
            Title = "New food discovered",
            Message = "Ranger discovered Stew"
        });

        Assert.True(created.TryRead(out var evt));
        Assert.Equal(dto.Id, evt.Id);
        Assert.Equal(TenantA, evt.TenantId);
        Assert.Equal("New food discovered", evt.Title);
    }

    [Fact]
    public async Task CreateAsync_TruncatesOverlongTitleAndMessage()
    {
        var dto = await _service.CreateAsync(new CreateNotificationDto
        {
            TenantId = TenantA,
            Type = "Test",
            Title = new string('T', 300),
            Message = new string('M', 1500)
        });

        Assert.Equal(200, dto.Title.Length);
        Assert.Equal(1000, dto.Message.Length);

        var row = await _db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == dto.Id);
        Assert.Equal(200, row.Title.Length);
        Assert.Equal(1000, row.Message.Length);
    }

    [Fact]
    public async Task DeleteExpiredAsync_BypassesTenantFilter_AndReturnsIds()
    {
        var now = DateTime.UtcNow;
        var expiredA = AddRow(TenantA, expiresAt: now.AddMinutes(-5));
        var expiredB = AddRow(TenantB, expiresAt: now.AddMinutes(-5));
        var liveA = AddRow(TenantA, expiresAt: now.AddDays(1));
        var neverExpires = AddRow(TenantA, expiresAt: null);
        await _db.SaveChangesAsync();

        // No HttpContext → the ambient tenant filter would match nothing; the fix
        // must still find and delete both tenants' expired rows.
        var deletedIds = await _service.DeleteExpiredAsync();

        Assert.Equal(
            new[] { expiredA.Id, expiredB.Id }.OrderBy(i => i),
            deletedIds.OrderBy(i => i));

        var remaining = await _db.Notifications.IgnoreQueryFilters().Select(n => n.Id).ToListAsync();
        Assert.Equal(
            new[] { liveA.Id, neverExpires.Id }.OrderBy(i => i),
            remaining.OrderBy(i => i));
    }

    private NotificationEntity AddRow(string tenantId, DateTime? expiresAt)
    {
        var row = new NotificationEntity
        {
            TenantId = tenantId,
            UserId = null,
            Type = "Test",
            Title = "t",
            Message = "m",
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            Priority = "Normal"
        };
        _db.Notifications.Add(row);
        return row;
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }
}
