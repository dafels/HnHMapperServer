using HnHMapperServer.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Data;

public sealed class ApplicationDbContext : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the current tenant ID from HttpContext.Items
    /// </summary>
    private string? GetCurrentTenantId()
    {
        return _httpContextAccessor?.HttpContext?.Items["TenantId"] as string;
    }

    /// <summary>
    /// Enables bypassing of tenant filters for superadmin operations
    /// </summary>
    public ApplicationDbContext BypassTenantFilter()
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return this;
    }

    public DbSet<GridDataEntity> Grids => Set<GridDataEntity>();
    public DbSet<MarkerEntity> Markers => Set<MarkerEntity>();
    public DbSet<TileDataEntity> Tiles => Set<TileDataEntity>();
    public DbSet<MapInfoEntity> Maps => Set<MapInfoEntity>();
    public DbSet<ConfigEntity> Config => Set<ConfigEntity>();
    public DbSet<TokenEntity> Tokens => Set<TokenEntity>();
    public DbSet<CustomMarkerEntity> CustomMarkers => Set<CustomMarkerEntity>();
    public DbSet<RoadEntity> Roads => Set<RoadEntity>();
    public DbSet<PingEntity> Pings => Set<PingEntity>();

    // Multi-tenancy tables
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<TenantUserEntity> TenantUsers => Set<TenantUserEntity>();
    public DbSet<TenantPermissionEntity> TenantPermissions => Set<TenantPermissionEntity>();
    public DbSet<TenantInvitationEntity> TenantInvitations => Set<TenantInvitationEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    // Notification and timer tables
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<TimerEntity> Timers => Set<TimerEntity>();
    public DbSet<TimerWarningEntity> TimerWarnings => Set<TimerWarningEntity>();
    public DbSet<NotificationPreferenceEntity> NotificationPreferences => Set<NotificationPreferenceEntity>();
    public DbSet<TimerHistoryEntity> TimerHistory => Set<TimerHistoryEntity>();

    // Overlay data tables
    public DbSet<OverlayDataEntity> OverlayData => Set<OverlayDataEntity>();

    // Overlay offset storage (for map comparison feature)
    public DbSet<OverlayOffsetEntity> OverlayOffsets => Set<OverlayOffsetEntity>();

    // Performance optimization tables
    public DbSet<DirtyZoomTileEntity> DirtyZoomTiles => Set<DirtyZoomTileEntity>();

    // Public map tables (global, not tenant-scoped)
    public DbSet<PublicMapEntity> PublicMaps => Set<PublicMapEntity>();
    public DbSet<PublicMapSourceEntity> PublicMapSources => Set<PublicMapSourceEntity>();

    // HMap source library tables (global, not tenant-scoped)
    public DbSet<HmapSourceEntity> HmapSources => Set<HmapSourceEntity>();
    public DbSet<PublicMapHmapSourceEntity> PublicMapHmapSources => Set<PublicMapHmapSourceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GridDataEntity>(entity =>
        {
            // COMPOSITE PRIMARY KEY: Allow same Grid ID across different tenants
            entity.HasKey(e => new { e.Id, e.TenantId });

            entity.Property(e => e.CoordX).IsRequired();
            entity.Property(e => e.CoordY).IsRequired();
            entity.Property(e => e.Map).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();

            entity.HasIndex(e => new { e.Map, e.CoordX, e.CoordY });
            entity.HasIndex(e => e.TenantId);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarkerEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.GridId).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();

            // COMPOSITE UNIQUE INDEX: Allow same Marker Key across different tenants
            entity.HasIndex(e => new { e.Key, e.TenantId }).IsUnique();
            entity.HasIndex(e => e.GridId);
            entity.HasIndex(e => e.TenantId);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TileDataEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MapId).IsRequired();
            entity.Property(e => e.CoordX).IsRequired();
            entity.Property(e => e.CoordY).IsRequired();
            entity.Property(e => e.Zoom).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.FileSizeBytes).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.OriginalTenantId).IsRequired(false);

            entity.HasIndex(e => new { e.MapId, e.Zoom, e.CoordX, e.CoordY }).IsUnique();
            entity.HasIndex(e => e.TenantId);

            // Optimized indexes for ZoomTileRebuildService queries
            // Supports FindMissingZoomTilesAsync and FindStaleZoomTilesAsync
            entity.HasIndex(e => new { e.TenantId, e.Zoom });
            entity.HasIndex(e => new { e.TenantId, e.MapId, e.Zoom });

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MapInfoEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();

            // Set default value for CreatedAt to CURRENT_TIMESTAMP in SQLite
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .IsRequired();

            // Index for efficient cleanup queries
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TenantId);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConfigEntity>(entity =>
        {
            // COMPOSITE PRIMARY KEY: Allow same Config Key across different tenants
            entity.HasKey(e => new { e.Key, e.TenantId });
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TokenEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .IsRequired();

            entity.Property(e => e.DisplayToken)
                .IsRequired();

            entity.Property(e => e.TokenHash)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .IsRequired();

            entity.Property(e => e.UserId)
                .IsRequired();

            entity.Property(e => e.Name)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.Scopes)
                .HasMaxLength(1024)
                .IsRequired(false);

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.LastUsedAt)
                .IsRequired(false);

            entity.Property(e => e.ExpiresAt)
                .IsRequired(false);

            entity.HasIndex(e => e.DisplayToken)
                .IsUnique();

            entity.HasIndex(e => e.TokenHash)
                .IsUnique();

            entity.HasIndex(e => e.TenantId);

            entity.HasIndex(e => new { e.UserId, e.Name })
                .IsUnique();

            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomMarkerEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasMaxLength(1000)
                .IsRequired(false);

            entity.Property(e => e.Icon)
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(e => e.CreatedBy)
                .IsRequired();

            entity.Property(e => e.PlacedAt)
                .IsRequired();

            entity.Property(e => e.UpdatedAt)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .IsRequired();

            entity.HasIndex(e => e.MapId);
            entity.HasIndex(e => e.GridId);
            entity.HasIndex(e => e.CreatedBy);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.MapId, e.CoordX, e.CoordY, e.X, e.Y }).IsUnique();
            entity.HasIndex(e => new { e.MapId, e.PlacedAt })
                .HasAnnotation("Sqlite:IndexColumnOrder", new[] { "ASC", "DESC" });

            // Composite foreign key to GridDataEntity (Id, TenantId)
            // Since Grids now has composite PK, we need to reference both columns
            entity.HasOne<GridDataEntity>()
                .WithMany()
                .HasForeignKey(e => new { e.GridId, e.TenantId })
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to MapInfoEntity
            entity.HasOne<MapInfoEntity>()
                .WithMany()
                .HasForeignKey(e => e.MapId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoadEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(e => e.Waypoints)
                .IsRequired();

            entity.Property(e => e.CreatedBy)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.UpdatedAt)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .IsRequired();

            entity.HasIndex(e => e.MapId);
            entity.HasIndex(e => e.CreatedBy);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.MapId, e.CreatedAt })
                .HasAnnotation("Sqlite:IndexColumnOrder", new[] { "ASC", "DESC" });

            // Foreign key to MapInfoEntity
            entity.HasOne<MapInfoEntity>()
                .WithMany()
                .HasForeignKey(e => e.MapId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PingEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.MapId)
                .IsRequired();

            entity.Property(e => e.CoordX)
                .IsRequired();

            entity.Property(e => e.CoordY)
                .IsRequired();

            entity.Property(e => e.X)
                .IsRequired();

            entity.Property(e => e.Y)
                .IsRequired();

            entity.Property(e => e.CreatedBy)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.ExpiresAt)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .IsRequired();

            // Indexes for efficient queries
            entity.HasIndex(e => e.MapId);
            entity.HasIndex(e => e.CreatedBy);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.ExpiresAt });

            // Foreign key to MapInfoEntity
            entity.HasOne<MapInfoEntity>()
                .WithMany()
                .HasForeignKey(e => e.MapId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Multi-tenancy entity configurations
        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.StorageQuotaMB).IsRequired().HasDefaultValue(1024);
            entity.Property(e => e.CurrentStorageMB).IsRequired().HasDefaultValue(0.0);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
        });

        modelBuilder.Entity<TenantUserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.UserId).IsRequired();

            // Map Role enum to RoleString column in database
            entity.Property(e => e.RoleString)
                .HasColumnName("Role")
                .IsRequired();
            entity.Ignore(e => e.Role); // Don't map the enum property directly

            entity.Property(e => e.JoinedAt).IsRequired();

            // Unique constraint on TenantId + UserId
            entity.HasIndex(e => new { e.TenantId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);

            // Foreign keys
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantPermissionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantUserId).IsRequired();

            // Map Permission enum to PermissionString column in database
            entity.Property(e => e.PermissionString)
                .HasColumnName("Permission")
                .IsRequired();
            entity.Ignore(e => e.Permission); // Don't map the enum property directly

            entity.HasIndex(e => e.TenantUserId);
            entity.HasIndex(e => e.PermissionString);

            // Foreign key - link to TenantUser.Permissions navigation property
            entity.HasOne<TenantUserEntity>()
                .WithMany(tu => tu.Permissions)
                .HasForeignKey(e => e.TenantUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantInvitationEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.InviteCode).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
            entity.Property(e => e.PendingApproval).IsRequired().HasDefaultValue(false);

            entity.HasIndex(e => e.InviteCode).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.ExpiresAt });

            // Foreign key
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLogEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.Action).IsRequired();

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.TenantId, e.Timestamp });
            entity.HasIndex(e => new { e.UserId, e.Timestamp });
            entity.HasIndex(e => e.Action);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });

        // Notification and timer entity configurations
        modelBuilder.Entity<NotificationEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Priority).IsRequired().HasDefaultValue("Normal");
            entity.Property(e => e.IsRead).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).IsRequired();

            // Indexes for efficient queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.IsRead });
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            entity.HasIndex(e => e.ExpiresAt);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to AspNetUsers (nullable)
            entity.HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
        });

        modelBuilder.Entity<TimerEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000).IsRequired(false);
            entity.Property(e => e.ReadyAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.IsCompleted).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.NotificationSent).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.PreExpiryWarningSent).IsRequired().HasDefaultValue(false);

            // Indexes for efficient queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.MarkerId);
            entity.HasIndex(e => e.CustomMarkerId);
            entity.HasIndex(e => new { e.TenantId, e.IsCompleted, e.ReadyAt });
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.IsCompleted });

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to AspNetUsers
            entity.HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to MarkerEntity (nullable)
            entity.HasOne<MarkerEntity>()
                .WithMany()
                .HasForeignKey(e => e.MarkerId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            // Foreign key to CustomMarkerEntity (nullable)
            entity.HasOne<CustomMarkerEntity>()
                .WithMany()
                .HasForeignKey(e => e.CustomMarkerId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        modelBuilder.Entity<TimerWarningEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TimerId).IsRequired();
            entity.Property(e => e.WarningMinutes).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();

            // Unique index to prevent duplicate warnings for same timer+interval
            entity.HasIndex(e => new { e.TimerId, e.WarningMinutes }).IsUnique();

            // Index for efficient queries
            entity.HasIndex(e => e.TimerId);

            // Foreign key to TimerEntity (cascade delete when timer is deleted)
            entity.HasOne<TimerEntity>()
                .WithMany()
                .HasForeignKey(e => e.TimerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationPreferenceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.NotificationType).IsRequired();
            entity.Property(e => e.Enabled).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.PlaySound).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.ShowBrowserNotification).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.PreExpiryWarningMinutes).IsRequired().HasDefaultValue(5);

            // Unique constraint: one preference per user per notification type
            entity.HasIndex(e => new { e.UserId, e.NotificationType }).IsUnique();

            // Foreign key to AspNetUsers
            entity.HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimerHistoryEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TimerId).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.CompletedAt).IsRequired();
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);

            // Indexes for efficient queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.TimerId);
            entity.HasIndex(e => e.MarkerId);
            entity.HasIndex(e => e.CustomMarkerId);
            entity.HasIndex(e => new { e.TenantId, e.CompletedAt });
            entity.HasIndex(e => new { e.TenantId, e.Type, e.CompletedAt });

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OverlayDataEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MapId).IsRequired();
            entity.Property(e => e.CoordX).IsRequired();
            entity.Property(e => e.CoordY).IsRequired();
            entity.Property(e => e.OverlayType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Data).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Unique index for (MapId, CoordX, CoordY, OverlayType, TenantId)
            entity.HasIndex(e => new { e.MapId, e.CoordX, e.CoordY, e.OverlayType, e.TenantId }).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.MapId);

            // Covering index for overlay queries - optimized for GetOverlaysForGridsAsync
            // Covers: TenantId (filter), MapId (filter), CoordX + CoordY (range lookups)
            entity.HasIndex(e => new { e.TenantId, e.MapId, e.CoordX, e.CoordY });

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to MapInfoEntity
            entity.HasOne<MapInfoEntity>()
                .WithMany()
                .HasForeignKey(e => e.MapId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OverlayOffsetEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.CurrentMapId).IsRequired();
            entity.Property(e => e.OverlayMapId).IsRequired();
            entity.Property(e => e.OffsetX).IsRequired();
            entity.Property(e => e.OffsetY).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Unique index: only one offset per map pair per tenant
            entity.HasIndex(e => new { e.TenantId, e.CurrentMapId, e.OverlayMapId }).IsUnique();
            entity.HasIndex(e => e.TenantId);

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DirtyZoomTileEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.MapId).IsRequired();
            entity.Property(e => e.CoordX).IsRequired();
            entity.Property(e => e.CoordY).IsRequired();
            entity.Property(e => e.Zoom).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            // Unique index: Only one dirty tile entry per (Tenant, Map, Coord, Zoom)
            // This ensures upserts can use "ON CONFLICT IGNORE" semantics
            entity.HasIndex(e => new { e.TenantId, e.MapId, e.CoordX, e.CoordY, e.Zoom }).IsUnique();

            // Index for efficient tenant queries (used by ZoomTileRebuildService)
            entity.HasIndex(e => e.TenantId);

            // Composite index for ordered processing
            entity.HasIndex(e => new { e.TenantId, e.Zoom, e.MapId });

            // Foreign key to Tenants
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to Maps
            entity.HasOne<MapInfoEntity>()
                .WithMany()
                .HasForeignKey(e => e.MapId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Public map entity configurations (global tables - no tenant filters)
        modelBuilder.Entity<PublicMapEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();

            // Generation settings
            entity.Property(e => e.AutoRegenerate).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.RegenerateIntervalMinutes).IsRequired(false);

            // Generation status
            entity.Property(e => e.GenerationStatus).IsRequired().HasDefaultValue("pending");
            entity.Property(e => e.LastGeneratedAt).IsRequired(false);
            entity.Property(e => e.LastGenerationDurationSeconds).IsRequired(false);
            entity.Property(e => e.TileCount).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.GenerationProgress).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.GenerationError).IsRequired(false);

            // Cached bounds
            entity.Property(e => e.MinX).IsRequired(false);
            entity.Property(e => e.MaxX).IsRequired(false);
            entity.Property(e => e.MinY).IsRequired(false);
            entity.Property(e => e.MaxY).IsRequired(false);

            // Indexes
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.GenerationStatus);
        });

        modelBuilder.Entity<PublicMapSourceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicMapId).IsRequired();
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.MapId).IsRequired();
            entity.Property(e => e.Priority).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.AddedAt).IsRequired();
            entity.Property(e => e.AddedBy).IsRequired();

            // Unique constraint: prevent duplicate sources
            entity.HasIndex(e => new { e.PublicMapId, e.TenantId, e.MapId }).IsUnique();
            entity.HasIndex(e => e.PublicMapId);
            entity.HasIndex(e => e.TenantId);

            // Foreign key to PublicMapEntity
            entity.HasOne<PublicMapEntity>()
                .WithMany()
                .HasForeignKey(e => e.PublicMapId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to TenantEntity
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HMap source library entity configurations (global tables - no tenant filters)
        modelBuilder.Entity<HmapSourceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.FileSizeBytes).IsRequired();
            entity.Property(e => e.UploadedAt).IsRequired();
            entity.Property(e => e.UploadedBy).IsRequired(false);

            // Cached analysis
            entity.Property(e => e.TotalGrids).IsRequired(false);
            entity.Property(e => e.SegmentCount).IsRequired(false);
            entity.Property(e => e.MinX).IsRequired(false);
            entity.Property(e => e.MaxX).IsRequired(false);
            entity.Property(e => e.MinY).IsRequired(false);
            entity.Property(e => e.MaxY).IsRequired(false);
            entity.Property(e => e.AnalyzedAt).IsRequired(false);

            // Indexes
            entity.HasIndex(e => e.UploadedAt);
            entity.HasIndex(e => e.Name);
        });

        modelBuilder.Entity<PublicMapHmapSourceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicMapId).IsRequired();
            entity.Property(e => e.HmapSourceId).IsRequired();
            entity.Property(e => e.Priority).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.AddedAt).IsRequired();

            // Per-source analysis
            entity.Property(e => e.NewGrids).IsRequired(false);
            entity.Property(e => e.OverlappingGrids).IsRequired(false);

            // Unique constraint: prevent duplicate sources per public map
            entity.HasIndex(e => new { e.PublicMapId, e.HmapSourceId }).IsUnique();
            entity.HasIndex(e => e.PublicMapId);
            entity.HasIndex(e => e.HmapSourceId);

            // Foreign key to PublicMapEntity
            entity.HasOne<PublicMapEntity>()
                .WithMany()
                .HasForeignKey(e => e.PublicMapId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to HmapSourceEntity
            entity.HasOne<HmapSourceEntity>()
                .WithMany()
                .HasForeignKey(e => e.HmapSourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Global query filters for automatic tenant isolation
        // These filters automatically add "WHERE TenantId = {currentTenantId}" to all queries
        // GetCurrentTenantId() is evaluated at query time (not model creation time)
        // Can be bypassed using IgnoreQueryFilters() for superadmin operations
        modelBuilder.Entity<MapInfoEntity>()
            .HasQueryFilter(m => m.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<GridDataEntity>()
            .HasQueryFilter(g => g.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<TileDataEntity>()
            .HasQueryFilter(t => t.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<MarkerEntity>()
            .HasQueryFilter(m => m.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<CustomMarkerEntity>()
            .HasQueryFilter(c => c.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<RoadEntity>()
            .HasQueryFilter(r => r.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<PingEntity>()
            .HasQueryFilter(p => p.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<TokenEntity>()
            .HasQueryFilter(t => t.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<ConfigEntity>()
            .HasQueryFilter(c => c.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<NotificationEntity>()
            .HasQueryFilter(n => n.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<TimerEntity>()
            .HasQueryFilter(t => t.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<TimerHistoryEntity>()
            .HasQueryFilter(t => t.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<OverlayDataEntity>()
            .HasQueryFilter(o => o.TenantId == GetCurrentTenantId());

        modelBuilder.Entity<OverlayOffsetEntity>()
            .HasQueryFilter(o => o.TenantId == GetCurrentTenantId());
    }
}

public sealed class GridDataEntity
{
    public string Id { get; set; } = string.Empty;
    public int CoordX { get; set; }
    public int CoordY { get; set; }
    public DateTime NextUpdate { get; set; }
    public int Map { get; set; }
    public string TenantId { get; set; } = string.Empty;
}

public sealed class MarkerEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GridId { get; set; } = string.Empty;
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public string Image { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public long MaxReady { get; set; }
    public long MinReady { get; set; }
    public bool Ready { get; set; }
    public string TenantId { get; set; } = string.Empty;
}

public sealed class TileDataEntity
{
    public int Id { get; set; }
    public int MapId { get; set; }
    public int CoordX { get; set; }
    public int CoordY { get; set; }
    public int Zoom { get; set; }
    public string File { get; set; } = string.Empty;
    public long Cache { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public int FileSizeBytes { get; set; } = 0;
    public string? OriginalTenantId { get; set; }
}

public sealed class MapInfoEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// UTC timestamp when the map was created (SQLite stores as TEXT in ISO 8601 format)
    /// Used for auto-cleanup of empty maps
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Default starting X coordinate when opening this map without URL parameters
    /// </summary>
    public int? DefaultStartX { get; set; }

    /// <summary>
    /// Default starting Y coordinate when opening this map without URL parameters
    /// </summary>
    public int? DefaultStartY { get; set; }

    public string TenantId { get; set; } = string.Empty;
}

public sealed class ConfigEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
}

public sealed class TokenEntity
{
    // Primary key (string GUID). Plaintext token is NEVER stored; only its hash.
    public string Id { get; set; } = string.Empty;

    // Full token with tenant prefix (e.g., "warrior-shield-42_abc123...")
    public string DisplayToken { get; set; } = string.Empty;

    // SHA-256 hex of the secret portion only (not including tenant prefix)
    public string TokenHash { get; set; } = string.Empty;

    // Tenant ID extracted from token prefix
    public string TenantId { get; set; } = string.Empty;

    // Owner (ASP.NET Core Identity user id)
    public string UserId { get; set; } = string.Empty;

    // Friendly display name/label (unique per user)
    public string Name { get; set; } = string.Empty;

    // Comma-separated scopes mapped to lowercase `auth` claims (e.g. "upload,map")
    public string? Scopes { get; set; }

    // Audit fields
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Represents a custom marker created by users on the map
/// </summary>
public sealed class CustomMarkerEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to MapInfoEntity
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Foreign key to GridDataEntity (string ID)
    /// </summary>
    public string GridId { get; set; } = string.Empty;

    /// <summary>
    /// Grid coordinate X (from Grids table)
    /// </summary>
    public int CoordX { get; set; }

    /// <summary>
    /// Grid coordinate Y (from Grids table)
    /// </summary>
    public int CoordY { get; set; }

    /// <summary>
    /// Position X within the grid (0-100)
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Position Y within the grid (0-100)
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Marker title (max 80 characters)
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional description (max 1000 characters)
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Icon name/path (must be from allowed icon list)
    /// </summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Username of the creator (from ASP.NET Identity)
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the marker was placed (immutable after creation)
    /// </summary>
    public DateTime PlacedAt { get; set; }

    /// <summary>
    /// UTC timestamp when the marker was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Whether the marker is hidden
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Represents a road/path created by users on the map
/// </summary>
public sealed class RoadEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to MapInfoEntity
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Road name (max 80 characters)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Waypoints as JSON array of coordinate objects
    /// Format: [{"coordX": 5, "coordY": 10, "x": 50, "y": 25}, ...]
    /// </summary>
    public string Waypoints { get; set; } = string.Empty;

    /// <summary>
    /// Username of the creator (from ASP.NET Identity)
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the road was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp when the road was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Whether the road is hidden
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Represents a temporary ping marker placed by users on the map
/// Auto-expires after 60 seconds
/// </summary>
public sealed class PingEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to MapInfoEntity
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Grid coordinate X
    /// </summary>
    public int CoordX { get; set; }

    /// <summary>
    /// Grid coordinate Y
    /// </summary>
    public int CoordY { get; set; }

    /// <summary>
    /// Position X within the grid (0-100)
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Position Y within the grid (0-100)
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Username of the creator (from ASP.NET Identity)
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the ping was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp when the ping expires (CreatedAt + 60 seconds)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Represents a notification for a user in the system
/// Notifications are shown in the notification center and can have actions
/// </summary>
public sealed class NotificationEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User ID who should receive this notification
    /// NULL means all users in the tenant
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Type of notification (MarkerTimerExpired, StandaloneTimerExpired, MapEvent, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Notification title (displayed prominently)
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Notification message/body
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Action type when notification is clicked (NavigateToMarker, NavigateToMap, OpenUrl, NoAction)
    /// </summary>
    public string? ActionType { get; set; }

    /// <summary>
    /// JSON string with action parameters (e.g., {markerId: 123, mapId: 5})
    /// </summary>
    public string? ActionData { get; set; }

    /// <summary>
    /// Whether the user has read this notification
    /// </summary>
    public bool IsRead { get; set; }

    /// <summary>
    /// When the notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the notification expires and should be auto-deleted
    /// NULL means never expires
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Priority level (Low, Normal, High, Urgent)
    /// </summary>
    public string Priority { get; set; } = "Normal";
}

/// <summary>
/// Represents a timer (either attached to a marker or standalone)
/// Timers generate notifications when they expire
/// </summary>
public sealed class TimerEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User ID who created this timer
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Type of timer (Marker, Standalone)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to MarkerEntity (NULL for standalone timers)
    /// </summary>
    public int? MarkerId { get; set; }

    /// <summary>
    /// Foreign key to CustomMarkerEntity (NULL for resource markers or standalone timers)
    /// </summary>
    public int? CustomMarkerId { get; set; }

    /// <summary>
    /// User-defined title for the timer
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the timer will expire (UTC timestamp)
    /// </summary>
    public DateTime ReadyAt { get; set; }

    /// <summary>
    /// When the timer was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Whether the timer has completed/expired
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Whether the expiry notification has been sent
    /// </summary>
    public bool NotificationSent { get; set; }

    /// <summary>
    /// Whether the pre-expiry warning notification has been sent
    /// </summary>
    public bool PreExpiryWarningSent { get; set; }
}

/// <summary>
/// User preferences for notifications
/// Controls which notification types the user wants to receive and how
/// </summary>
public sealed class NotificationPreferenceEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Type of notification (MarkerTimer, StandaloneTimer, MapEvent, etc.)
    /// </summary>
    public string NotificationType { get; set; } = string.Empty;

    /// <summary>
    /// Whether this notification type is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to play sound for this notification type
    /// </summary>
    public bool PlaySound { get; set; } = false;

    /// <summary>
    /// Whether to show browser push notification
    /// </summary>
    public bool ShowBrowserNotification { get; set; } = false;

    /// <summary>
    /// Minutes before timer expires to show pre-expiry warning (for timer notifications)
    /// </summary>
    public int PreExpiryWarningMinutes { get; set; } = 5;
}

/// <summary>
/// Historical record of completed timers
/// Used for learning resource respawn patterns
/// </summary>
public sealed class TimerHistoryEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Original timer ID
    /// </summary>
    public int TimerId { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// When the timer completed/expired
    /// </summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>
    /// Actual duration in minutes from creation to completion
    /// </summary>
    public int? Duration { get; set; }

    /// <summary>
    /// Type of timer (Marker, Standalone)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Marker ID if it was a marker timer
    /// </summary>
    public int? MarkerId { get; set; }

    /// <summary>
    /// Custom marker ID if it was a custom marker timer
    /// </summary>
    public int? CustomMarkerId { get; set; }

    /// <summary>
    /// Timer title (copied from TimerEntity)
    /// </summary>
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// Tracks which pre-expiry warnings have been sent for a timer
/// Supports multiple warnings at different intervals (1 day, 4 hours, 1 hour, 10 minutes)
/// </summary>
public sealed class TimerWarningEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to TimerEntity
    /// </summary>
    public int TimerId { get; set; }

    /// <summary>
    /// Warning interval in minutes (e.g., 1440 for 1 day, 60 for 1 hour)
    /// </summary>
    public int WarningMinutes { get; set; }

    /// <summary>
    /// UTC timestamp when the warning was sent
    /// </summary>
    public DateTime SentAt { get; set; }
}

/// <summary>
/// Tracks zoom tiles that need rebuilding after base tile uploads.
/// Instead of scanning all tiles to find stale ones, we mark dirty tiles on upload.
/// </summary>
public sealed class DirtyZoomTileEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Map ID the tile belongs to
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Tile coordinate X (at the given zoom level)
    /// </summary>
    public int CoordX { get; set; }

    /// <summary>
    /// Tile coordinate Y (at the given zoom level)
    /// </summary>
    public int CoordY { get; set; }

    /// <summary>
    /// Zoom level (1-6)
    /// </summary>
    public int Zoom { get; set; }

    /// <summary>
    /// When the tile was marked dirty (for debugging/monitoring)
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Stores overlay data (claims, villages, provinces) for a grid coordinate
/// Data is bitpacked: 1250 bytes for 100x100 tiles, 1 bit per tile
/// </summary>
public sealed class OverlayDataEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to MapInfoEntity
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Grid coordinate X
    /// </summary>
    public int CoordX { get; set; }

    /// <summary>
    /// Grid coordinate Y
    /// </summary>
    public int CoordY { get; set; }

    /// <summary>
    /// Overlay type (e.g., "ClaimFloor", "VillageOutline", "Province0")
    /// </summary>
    public string OverlayType { get; set; } = string.Empty;

    /// <summary>
    /// Bitpacked overlay data (1250 bytes for 100x100 grid, LSB first)
    /// Each bit represents whether the overlay is present at that tile position.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the overlay data was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Stores overlay offset mappings for map comparison feature.
/// One entry per (CurrentMapId, OverlayMapId) pair per tenant.
/// </summary>
public sealed class OverlayOffsetEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The base map ID (the main map being viewed)
    /// </summary>
    public int CurrentMapId { get; set; }

    /// <summary>
    /// The overlay map ID (the map being overlaid on top)
    /// </summary>
    public int OverlayMapId { get; set; }

    /// <summary>
    /// Horizontal offset in grid coordinates
    /// </summary>
    public double OffsetX { get; set; }

    /// <summary>
    /// Vertical offset in grid coordinates
    /// </summary>
    public double OffsetY { get; set; }

    /// <summary>
    /// UTC timestamp when this offset was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Represents a public map that combines tiles from multiple tenant maps.
/// This is a global table - not tenant-scoped.
/// </summary>
public sealed class PublicMapEntity
{
    /// <summary>
    /// Primary key - URL slug (e.g., "world-map")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the public map
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the public map is active and accessible
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// UTC timestamp when the public map was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// User ID of the SuperAdmin who created this public map
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Whether to automatically regenerate on a schedule
    /// </summary>
    public bool AutoRegenerate { get; set; }

    /// <summary>
    /// Interval in minutes for automatic regeneration (if AutoRegenerate is true)
    /// </summary>
    public int? RegenerateIntervalMinutes { get; set; }

    /// <summary>
    /// Current generation status: pending, running, completed, failed
    /// </summary>
    public string GenerationStatus { get; set; } = "pending";

    /// <summary>
    /// UTC timestamp when generation last completed
    /// </summary>
    public DateTime? LastGeneratedAt { get; set; }

    /// <summary>
    /// Duration of the last generation in seconds
    /// </summary>
    public int? LastGenerationDurationSeconds { get; set; }

    /// <summary>
    /// Number of tiles generated
    /// </summary>
    public int TileCount { get; set; }

    /// <summary>
    /// Current generation progress (0-100)
    /// </summary>
    public int GenerationProgress { get; set; }

    /// <summary>
    /// Error message if generation failed
    /// </summary>
    public string? GenerationError { get; set; }

    /// <summary>
    /// Minimum X coordinate (cached bounds)
    /// </summary>
    public int? MinX { get; set; }

    /// <summary>
    /// Maximum X coordinate (cached bounds)
    /// </summary>
    public int? MaxX { get; set; }

    /// <summary>
    /// Minimum Y coordinate (cached bounds)
    /// </summary>
    public int? MinY { get; set; }

    /// <summary>
    /// Maximum Y coordinate (cached bounds)
    /// </summary>
    public int? MaxY { get; set; }
}

/// <summary>
/// Represents a source map that contributes tiles to a public map.
/// This is a global table - not tenant-scoped.
/// </summary>
public sealed class PublicMapSourceEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to PublicMapEntity
    /// </summary>
    public string PublicMapId { get; set; } = string.Empty;

    /// <summary>
    /// Tenant ID of the source map
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Map ID within the tenant
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// Display priority (higher = displayed first in UI)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// UTC timestamp when this source was added
    /// </summary>
    public DateTime AddedAt { get; set; }

    /// <summary>
    /// User ID of the SuperAdmin who added this source
    /// </summary>
    public string AddedBy { get; set; } = string.Empty;
}

/// <summary>
/// Represents an uploaded HMap file in the shared source library.
/// HMap files can be selected as sources for public map generation.
/// This is a global table - not tenant-scoped.
/// </summary>
public sealed class HmapSourceEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display name for the HMap source
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Original filename when uploaded
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Path to the stored .hmap file (relative to GridStorage)
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// UTC timestamp when the file was uploaded
    /// </summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// User ID of the SuperAdmin who uploaded this file
    /// </summary>
    public string? UploadedBy { get; set; }

    // Cached analysis (from parsing)

    /// <summary>
    /// Total number of grids in the HMap file
    /// </summary>
    public int? TotalGrids { get; set; }

    /// <summary>
    /// Number of distinct segments in the HMap file
    /// </summary>
    public int? SegmentCount { get; set; }

    /// <summary>
    /// Minimum X coordinate across all grids
    /// </summary>
    public int? MinX { get; set; }

    /// <summary>
    /// Maximum X coordinate across all grids
    /// </summary>
    public int? MaxX { get; set; }

    /// <summary>
    /// Minimum Y coordinate across all grids
    /// </summary>
    public int? MinY { get; set; }

    /// <summary>
    /// Maximum Y coordinate across all grids
    /// </summary>
    public int? MaxY { get; set; }

    /// <summary>
    /// UTC timestamp when the file was last analyzed
    /// </summary>
    public DateTime? AnalyzedAt { get; set; }
}

/// <summary>
/// Links public maps to their selected HMap sources for generation.
/// This is a global table - not tenant-scoped.
/// </summary>
public sealed class PublicMapHmapSourceEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to PublicMapEntity
    /// </summary>
    public string PublicMapId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to HmapSourceEntity
    /// </summary>
    public int HmapSourceId { get; set; }

    /// <summary>
    /// Priority for merge ordering (higher priority sources overwrite lower)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// UTC timestamp when this source was added to the public map
    /// </summary>
    public DateTime AddedAt { get; set; }

    // Per-source analysis (vs this public map's other sources)

    /// <summary>
    /// Number of grids this source would add (not overlapping with other sources)
    /// </summary>
    public int? NewGrids { get; set; }

    /// <summary>
    /// Number of grids that overlap with other selected sources
    /// </summary>
    public int? OverlappingGrids { get; set; }
}
