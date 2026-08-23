using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for CustomMarkerService - validates authorization, validation, and CRUD operations
/// </summary>
public class CustomMarkerServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ICustomMarkerRepository _customMarkerRepository;
    private readonly ICustomMarkerService _customMarkerService;
    private readonly Mock<IIconCatalogService> _mockIconCatalog;

    private const string TestTenantId = "test-tenant-1";

    public CustomMarkerServiceTests()
    {
        // Create mock HttpContextAccessor to set tenant ID for EF Core query filters
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        // Create in-memory database for testing
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options, mockHttpContextAccessor.Object);

        // Mock tenant context accessor with a test tenant
        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        // Initialize repository
        _customMarkerRepository = new CustomMarkerRepository(_dbContext, mockTenantContext.Object);

        // Mock icon catalog service to return test icons
        _mockIconCatalog = new Mock<IIconCatalogService>();
        _mockIconCatalog.Setup(x => x.GetIconsAsync())
            .ReturnsAsync(new List<string>
            {
                "gfx/icons/arrow.png",
                "gfx/icons/player/player-0.png",
                "gfx/terobjs/mm/custom.png"
            });

        // Initialize service
        _customMarkerService = new CustomMarkerService(
            _customMarkerRepository,
            _dbContext,
            _mockIconCatalog.Object,
            NullLogger<CustomMarkerService>.Instance);

        // Seed test data
        SeedTestData();
    }

    /// <summary>
    /// Seed database with test maps and grids
    /// </summary>
    private void SeedTestData()
    {
        // Create a test tenant
        var tenant = new TenantEntity
        {
            Id = TestTenantId,
            Name = "Test Tenant",
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _dbContext.Tenants.Add(tenant);

        // Create a test map
        var map = new MapInfoEntity
        {
            Id = 1,
            Name = "Test Map",
            Hidden = false,
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            TenantId = TestTenantId
        };
        _dbContext.Maps.Add(map);

        // Create a test grid
        var grid = new GridDataEntity
        {
            Id = "0_0",
            CoordX = 0,
            CoordY = 0,
            Map = 1,
            NextUpdate = DateTime.UtcNow.AddDays(1),
            TenantId = TestTenantId
        };
        _dbContext.Grids.Add(grid);

        _dbContext.SaveChanges();
    }

    /// <summary>
    /// Test: Creating a valid custom marker succeeds
    /// </summary>
    [Fact]
    public async Task CreateAsync_ValidMarker_Succeeds()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "Test Marker",
            Description = "Test description",
            Icon = "gfx/icons/arrow.png"
        };

        // Act
        var result = await _customMarkerService.CreateAsync(dto, "testuser");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Marker", result.Title);
        Assert.Equal("Test description", result.Description);
        Assert.Equal("testuser", result.CreatedBy);
        Assert.True(result.PlacedAt <= DateTime.UtcNow);
        Assert.True(result.CanEdit); // testuser is the creator, so can edit
    }

    /// <summary>
    /// Test: Creating marker with invalid icon fails
    /// </summary>
    [Fact]
    public async Task CreateAsync_InvalidIcon_ThrowsArgumentException()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "Test Marker",
            Icon = "invalid-icon.png" // Not in whitelist
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _customMarkerService.CreateAsync(dto, "testuser"));
    }

    /// <summary>
    /// Test: Creating marker with title too long fails
    /// </summary>
    [Fact]
    public async Task CreateAsync_TitleTooLong_ThrowsArgumentException()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = new string('A', 81), // 81 chars, max is 80
            Icon = "gfx/icons/arrow.png"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _customMarkerService.CreateAsync(dto, "testuser"));
    }

    /// <summary>
    /// Test: Creating marker on non-existent grid fails
    /// </summary>
    [Fact]
    public async Task CreateAsync_GridNotFound_ThrowsInvalidOperationException()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 99,  // Grid doesn't exist
            CoordY = 99,
            X = 50,
            Y = 50,
            Title = "Test Marker",
            Icon = "gfx/icons/arrow.png"
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _customMarkerService.CreateAsync(dto, "testuser"));
    }

    /// <summary>
    /// Test: Updating own marker succeeds
    /// </summary>
    [Fact]
    public async Task UpdateAsync_OwnMarker_Succeeds()
    {
        // Arrange - Create a marker first
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "Original Title",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "testuser");

        var updateDto = new UpdateCustomMarkerDto
        {
            Title = "Updated Title",
            Description = "Updated description",
            Icon = "gfx/terobjs/mm/custom.png",
            Hidden = false
        };

        // Act
        var result = await _customMarkerService.UpdateAsync(created.Id, updateDto, "testuser", false);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Title", result.Title);
        Assert.Equal("Updated description", result.Description);
        Assert.Equal(created.PlacedAt, result.PlacedAt); // PlacedAt should be immutable
        Assert.True(result.UpdatedAt > result.PlacedAt); // UpdatedAt should be newer
    }

    /// <summary>
    /// Test: Updating someone else's marker without admin fails
    /// </summary>
    [Fact]
    public async Task UpdateAsync_OtherUserMarkerWithoutAdmin_ThrowsUnauthorizedAccessException()
    {
        // Arrange - Create a marker as user1
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "User1 Marker",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "user1");

        var updateDto = new UpdateCustomMarkerDto
        {
            Title = "Hacked Title",
            Icon = "gfx/icons/arrow.png",
            Hidden = false
        };

        // Act & Assert - Try to update as user2 (not admin)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await _customMarkerService.UpdateAsync(created.Id, updateDto, "user2", isAdmin: false));
    }

    /// <summary>
    /// Test: Admin can update anyone's marker
    /// </summary>
    [Fact]
    public async Task UpdateAsync_AdminUpdatesOtherUserMarker_Succeeds()
    {
        // Arrange - Create a marker as user1
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "User1 Marker",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "user1");

        var updateDto = new UpdateCustomMarkerDto
        {
            Title = "Admin Updated",
            Icon = "gfx/icons/arrow.png",
            Hidden = true
        };

        // Act - Update as admin
        var result = await _customMarkerService.UpdateAsync(created.Id, updateDto, "admin", isAdmin: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Admin Updated", result.Title);
        Assert.True(result.Hidden);
        Assert.Equal("user1", result.CreatedBy); // Creator should not change
    }

    /// <summary>
    /// Test: Deleting own marker succeeds
    /// </summary>
    [Fact]
    public async Task DeleteAsync_OwnMarker_Succeeds()
    {
        // Arrange
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "To Delete",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "testuser");

        // Act
        await _customMarkerService.DeleteAsync(created.Id, "testuser", false);

        // Assert - Verify marker is deleted
        var marker = await _customMarkerRepository.GetByIdAsync(created.Id);
        Assert.Null(marker);
    }

    /// <summary>
    /// Test: Deleting someone else's marker without admin fails
    /// </summary>
    [Fact]
    public async Task DeleteAsync_OtherUserMarkerWithoutAdmin_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "User1 Marker",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "user1");

        // Act & Assert - Try to delete as user2 (not admin)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await _customMarkerService.DeleteAsync(created.Id, "user2", isAdmin: false));
    }

    /// <summary>
    /// Test: HTML tags are stripped from title and description
    /// </summary>
    [Fact]
    public async Task CreateAsync_HTMLInInput_Sanitizes()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "<script>alert('xss')</script>Clean Title",
            Description = "<b>Bold</b> text with <a href='#'>link</a>",
            Icon = "gfx/icons/arrow.png"
        };

        // Act
        var result = await _customMarkerService.CreateAsync(dto, "testuser");

        // Assert - HTML should be stripped
        Assert.Equal("Clean Title", result.Title);
        Assert.Equal("Bold text with link", result.Description);
    }

    /// <summary>
    /// Test: script stripping is case-insensitive and spans newlines.
    /// <para>
    /// These two properties come from RegexOptions.IgnoreCase and RegexOptions.Singleline.
    /// They are asserted separately because the sanitizer's pattern moved from an inline
    /// Regex.Replace call to [GeneratedRegex], which carries its options in an attribute —
    /// dropping one there would silently let markup through, and this is XSS-facing code.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateAsync_ScriptVariants_AreStripped()
    {
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            // Uppercase tag with an attribute, and a body broken across lines.
            Title = "<SCRIPT TYPE=\"text/javascript\">alert('x')</SCRIPT>Kept",
            Description = "before<script>\nvar a = 1;\nalert('multi');\n</script>after",
            Icon = "gfx/icons/arrow.png"
        };

        var result = await _customMarkerService.CreateAsync(dto, "testuser");

        Assert.Equal("Kept", result.Title);
        Assert.Equal("beforeafter", result.Description);
        Assert.DoesNotContain("alert", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test: Coordinates are clamped to 0-100 range
    /// </summary>
    [Fact]
    public async Task CreateAsync_InvalidCoordinates_Clamps()
    {
        // Arrange
        var dto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 150,  // Over 100
            Y = -10,  // Under 0
            Title = "Test Marker",
            Icon = "gfx/icons/arrow.png"
        };

        // Act
        var result = await _customMarkerService.CreateAsync(dto, "testuser");

        // Assert - Coordinates should be clamped
        Assert.Equal(100, result.X); // Clamped to max
        Assert.Equal(0, result.Y);   // Clamped to min
    }

    /// <summary>
    /// Test: PlacedAt timestamp is set and immutable
    /// </summary>
    [Fact]
    public async Task UpdateAsync_PlacedAtIsImmutable()
    {
        // Arrange - Create a marker
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "Test Marker",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "testuser");
        var originalPlacedAt = created.PlacedAt;

        // Wait a bit to ensure time difference
        await Task.Delay(100);

        var updateDto = new UpdateCustomMarkerDto
        {
            Title = "Updated",
            Icon = "gfx/icons/arrow.png",
            Hidden = false
        };

        // Act
        var updated = await _customMarkerService.UpdateAsync(created.Id, updateDto, "testuser", false);

        // Assert - PlacedAt should not change
        Assert.Equal(originalPlacedAt, updated.PlacedAt);
        Assert.True(updated.UpdatedAt > updated.PlacedAt); // UpdatedAt should be newer
    }

    /// <summary>
    /// Test: GetByMapIdAsync returns markers for correct map
    /// </summary>
    [Fact]
    public async Task GetByMapIdAsync_ReturnsCorrectMarkers()
    {
        // Arrange - Create markers on different maps
        // First create a second map and grid
        var map2 = new MapInfoEntity
        {
            Id = 2,
            Name = "Map 2",
            Hidden = false,
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            TenantId = TestTenantId
        };
        _dbContext.Maps.Add(map2);

        var grid2 = new GridDataEntity
        {
            Id = "5_5",
            CoordX = 5,
            CoordY = 5,
            Map = 2,
            NextUpdate = DateTime.UtcNow.AddDays(1),
            TenantId = TestTenantId
        };
        _dbContext.Grids.Add(grid2);
        _dbContext.SaveChanges();

        // Create markers on different maps
        var marker1 = await _customMarkerService.CreateAsync(new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 10,
            Y = 10,
            Title = "Marker on Map 1",
            Icon = "gfx/icons/arrow.png"
        }, "testuser");

        var marker2 = await _customMarkerService.CreateAsync(new CreateCustomMarkerDto
        {
            MapId = 2,
            CoordX = 5,
            CoordY = 5,
            X = 20,
            Y = 20,
            Title = "Marker on Map 2",
            Icon = "gfx/icons/arrow.png"
        }, "testuser");

        // Act
        var map1Markers = await _customMarkerService.GetByMapIdAsync(1, "testuser", false);

        // Assert
        Assert.Single(map1Markers);
        Assert.Equal("Marker on Map 1", map1Markers[0].Title);
    }

    /// <summary>
    /// Test: CanEdit flag is set correctly for creator and admin
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_CanEditFlag_SetCorrectly()
    {
        // Arrange - Create a marker
        var createDto = new CreateCustomMarkerDto
        {
            MapId = 1,
            CoordX = 0,
            CoordY = 0,
            X = 50,
            Y = 50,
            Title = "Test Marker",
            Icon = "gfx/icons/arrow.png"
        };
        var created = await _customMarkerService.CreateAsync(createDto, "creator");

        // Act & Assert - Creator can edit
        var asCreator = await _customMarkerService.GetByIdAsync(created.Id, "creator", isAdmin: false);
        Assert.True(asCreator?.CanEdit);

        // Act & Assert - Other user cannot edit
        var asOtherUser = await _customMarkerService.GetByIdAsync(created.Id, "otheruser", isAdmin: false);
        Assert.False(asOtherUser?.CanEdit);

        // Act & Assert - Admin can edit
        var asAdmin = await _customMarkerService.GetByIdAsync(created.Id, "otheruser", isAdmin: true);
        Assert.True(asAdmin?.CanEdit);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}

