using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Api.BackgroundServices;
using HnHMapperServer.Api.Endpoints;
using HnHMapperServer.Api.Middleware;
using HnHMapperServer.Api.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Serilog.Events;
using SixLabors.ImageSharp;
using HnHMapperServer.Core.Models;
using System.Text.Json;
using System.Threading.RateLimiting;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Enums;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for long-lived SSE connections and global resource limits
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Disable MinResponseDataRate globally to allow SSE and other streaming endpoints
    // SSE heartbeat sends only ~15 bytes every 15 seconds, which is below the default 240 bytes/second
    serverOptions.Limits.MinResponseDataRate = null;

    // Global connection limits to prevent server overload
    // Production: generous limits, rely on rate limiting for abuse prevention
    serverOptions.Limits.MaxConcurrentConnections = 10000;          // Total HTTP connections
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 500;    // WebSocket/SSE connections

    // Allow large file uploads for .hmap import (up to 1GB)
    serverOptions.Limits.MaxRequestBodySize = 1024 * 1024 * 1024;  // 1GB
});

// Configure form options for large file uploads (.hmap files can be 500MB+)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024 * 1024 * 1024; // 1GB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Configure ImageSharp for better resource management during zoom tile generation
// Use default ArrayPool-based allocator with reasonable limits
SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism = 2;  // Limit parallel image processing to reduce memory spikes

// Configure Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Add Aspire service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Add services to the container
var configuredGridStorage = builder.Configuration["GridStorage"]; // raw from config/env
var gridStorage = configuredGridStorage;
if (string.IsNullOrWhiteSpace(gridStorage))
{
    // Default to shared solution-level path so Web and API use identical storage
    gridStorage = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "map"));
}
else if (!Path.IsPathRooted(gridStorage))
{
    // Resolve relative GridStorage consistently to solution-level path
    gridStorage = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", gridStorage));
}
// SQLite connection with WAL mode for better concurrency during imports
// - Cache=Shared: Shared cache for better connection reuse
// - Pooling=True: Connection pooling for performance
var dbPath = Path.Combine(gridStorage, "grids.db");
var connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True";

builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    if (env.IsDevelopment())
    {
        options.EnableDetailedErrors();
        options.EnableSensitiveDataLogging();
    }
    options.UseSqlite(connectionString, sqliteOptions =>
    {
        sqliteOptions.CommandTimeout(60); // 60 second timeout for long import operations
    });

    // Disable EF Core command logging completely
    options.ConfigureWarnings(warnings =>
        warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted));
});

// Enable WAL mode and set busy timeout on startup for better concurrency
builder.Services.AddHostedService<HnHMapperServer.Api.BackgroundServices.SqliteWalInitializerService>();

// Register repositories (non-auth repositories only)
builder.Services.AddScoped<IGridRepository, GridRepository>();
builder.Services.AddScoped<IMarkerRepository, MarkerRepository>();
builder.Services.AddScoped<ITileRepository, TileRepository>();
builder.Services.AddScoped<IMapRepository, MapRepository>();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();
builder.Services.AddScoped<ICustomMarkerRepository, CustomMarkerRepository>();
builder.Services.AddScoped<IRoadRepository, RoadRepository>();
builder.Services.AddScoped<IPingRepository, PingRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ITenantInvitationRepository, TenantInvitationRepository>();
builder.Services.AddScoped<IOverlayDataRepository, OverlayDataRepository>();
builder.Services.AddScoped<IOverlayOffsetRepository, OverlayOffsetRepository>();

// Register services
// UpdateNotificationService must be registered before CharacterService (dependency)
builder.Services.AddSingleton<IUpdateNotificationService, UpdateNotificationService>();
builder.Services.AddSingleton<ICharacterService, CharacterService>();
builder.Services.AddSingleton<HnHMapperServer.Api.Services.MapRevisionCache>();
builder.Services.AddSingleton<IBuildInfoProvider, BuildInfoProvider>();
builder.Services.AddSingleton<IPendingMarkerService, PendingMarkerService>();  // In-memory queue for markers before grids exist
builder.Services.AddSingleton<ITenantActivityService, TenantActivityService>();  // Tenant activity tracking with in-memory cache
builder.Services.AddScoped<ITileService, TileService>();
builder.Services.AddScoped<IGridService, GridService>();
builder.Services.AddScoped<IMarkerService, MarkerService>();
builder.Services.AddScoped<ICustomMarkerService, CustomMarkerService>();
builder.Services.AddScoped<IRoadService, RoadService>();
builder.Services.AddScoped<IPingService, PingService>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<TokenMigrationService>();
builder.Services.AddScoped<TenantNameService>();
builder.Services.AddScoped<IMapNameService, MapNameService>();
builder.Services.AddScoped<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddScoped<IStorageQuotaService, StorageQuotaService>();
builder.Services.AddScoped<ITenantFilePathService, TenantFilePathService>();
builder.Services.AddScoped<IAuditService, AuditService>();  // Phase 6: Audit logging service
builder.Services.AddScoped<INotificationService, NotificationService>();  // Notification system
builder.Services.AddScoped<ITimerService, TimerService>();  // Timer system
builder.Services.AddScoped<ITimerWarningService, TimerWarningService>();  // Timer warning tracking
builder.Services.AddScoped<IHmapImportService, HmapImportService>();  // .hmap file import service
builder.Services.AddSingleton<ImportLockService>();  // Import lock and cooldown management

// Large tile service for 400x400 WebP tiles (private maps)
builder.Services.AddScoped<ILargeTileService, LargeTileService>();

// Public maps services (global, not tenant-scoped)
builder.Services.AddScoped<IPublicMapService, PublicMapService>();
builder.Services.AddSingleton<IPublicMapGenerationService, PublicMapGenerationService>();

// Register memory cache for preview URL signing service
builder.Services.AddMemoryCache();

// Register HttpClient factory for Discord webhook service
builder.Services.AddHttpClient();

// Register Discord webhook service
builder.Services.AddScoped<IDiscordWebhookService, DiscordWebhookService>();

// Register map preview service for Discord notifications
builder.Services.AddScoped<IMapPreviewService, MapPreviewService>();

// Register preview URL signing service for secure preview access (depends on IMemoryCache)
builder.Services.AddScoped<IPreviewUrlSigningService, PreviewUrlSigningService>();

// Register TileCacheService (singleton to cache tiles in memory and prevent blocking SSE connections)
builder.Services.AddSingleton<TileCacheService>();

// Register IconCatalogService with container-friendly path resolution
// Priority:
// 1) IconCatalog:WwwrootPath (explicit override via env/config)
// 2) API container local path: /app/wwwroot (copied in Dockerfile)
// 3) Dev fallback: ../HnHMapperServer.Web/wwwroot
var iconCatalogOverride = builder.Configuration["IconCatalog:WwwrootPath"];
var iconPathCandidates = new[]
{
    iconCatalogOverride,
    Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "HnHMapperServer.Web", "wwwroot"))
};

var selectedIconWwwroot = iconPathCandidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p!))
                         ?? iconPathCandidates[^1]!;

builder.Services.AddSingleton<IIconCatalogService>(sp =>
    new IconCatalogService(selectedIconWwwroot, sp.GetRequiredService<ILogger<IconCatalogService>>()));

// Register background services
builder.Services.AddHostedService<CharacterCleanupService>();
builder.Services.AddHostedService<MarkerReadinessService>();
//builder.Services.AddHostedService<MapCleanupService>();
builder.Services.AddHostedService<InvitationExpirationService>();
builder.Services.AddHostedService<TenantStorageVerificationService>(); // Phase 4: Storage quota verification
builder.Services.AddHostedService<PingCleanupService>(); // Ping cleanup service
builder.Services.AddHostedService<ZoomTileRebuildService>(); // Zoom tile rebuild service
builder.Services.AddHostedService<TimerCheckService>(); // Timer monitoring and notification service
builder.Services.AddHostedService<PreviewCleanupService>(); // Map preview cleanup service (7 day retention)
builder.Services.AddHostedService<HmapTempCleanupService>(); // HMAP temp file cleanup service (7 day retention)
builder.Services.AddHostedService<OrphanedMarkerCleanupService>(); // Orphaned marker cleanup service
builder.Services.AddHostedService<TenantActivityFlushService>(); // Tenant activity flush service (2-min interval)
builder.Services.AddHostedService<PublicMapGenerationBackgroundService>(); // Public map tile generation
// Disabled: On-demand generation with caching is now efficient enough
// builder.Services.AddHostedService<LargeTileGenerationService>(); // Private map 400x400 WebP tile generation

// Configure shared data protection for cookie sharing with Web
var dataProtectionPath = Path.Combine(
    gridStorage,
    "DataProtection-Keys");

Directory.CreateDirectory(dataProtectionPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("HnHMapper");

// Add ASP.NET Core Identity with roles and EF stores
builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = false;
        // Password policy: 6+ characters minimum
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false; // No special character required
        options.Password.RequireUppercase = false;
        options.Password.RequiredLength = 6;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<HnHMapperServer.Api.Security.TenantClaimsPrincipalFactory>();

// Configure security stamp validation interval for fast role/permission updates
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromSeconds(10);
});

// Configure Identity's ApplicationCookie to match Web's cookie settings
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "HnH.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/auth/login";
    options.LogoutPath = "/api/auth/logout";
    options.AccessDeniedPath = "/api/auth/login";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/admin") ||
            context.Request.Path.StartsWithSegments("/map/api") ||
            context.Request.Path.StartsWithSegments("/map/updates"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/admin") ||
            context.Request.Path.StartsWithSegments("/map/api") ||
            context.Request.Path.StartsWithSegments("/map/updates"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

// Register authorization handlers for Phase 5 (multi-tenancy RBAC)
builder.Services.AddScoped<IAuthorizationHandler, SuperadminOnlyHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TenantAdminHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TenantPermissionHandler>();

builder.Services.AddAuthorization(options =>
{
    // Multi-tenancy authorization policies using type-safe constants and enums

    // Superadmin policy - global access to all tenants
    options.AddPolicy(AuthorizationConstants.Policies.SuperAdminOnly, policy =>
        policy.Requirements.Add(new SuperadminOnlyRequirement()));

    // Tenant admin policy - manage users within tenant
    options.AddPolicy(AuthorizationConstants.Policies.TenantAdmin, policy =>
        policy.Requirements.Add(new TenantAdminRequirement()));

    // Tenant permission policies - granular permissions per user (using Permission enum)
    options.AddPolicy(AuthorizationConstants.Policies.TenantMapAccess, policy =>
        policy.Requirements.Add(new TenantPermissionRequirement(Permission.Map)));

    options.AddPolicy(AuthorizationConstants.Policies.TenantMarkersAccess, policy =>
        policy.Requirements.Add(new TenantPermissionRequirement(Permission.Markers)));

    options.AddPolicy(AuthorizationConstants.Policies.TenantPointerAccess, policy =>
        policy.Requirements.Add(new TenantPermissionRequirement(Permission.Pointer)));

    options.AddPolicy(AuthorizationConstants.Policies.TenantUpload, policy =>
        policy.Requirements.Add(new TenantPermissionRequirement(Permission.Upload)));

    options.AddPolicy(AuthorizationConstants.Policies.TenantWriter, policy =>
        policy.Requirements.Add(new TenantPermissionRequirement(Permission.Writer)));
});

// Phase 7: Rate limiting configuration
builder.Services.AddRateLimiter(options =>
{
    // Per-tenant rate limit (100 requests per minute)
    options.AddPolicy("PerTenant", httpContext =>
    {
        var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: tenantId,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5000,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            });
    });

    // Expensive operations: Tile uploads (20 per minute per tenant)
    options.AddPolicy("TileUpload", httpContext =>
    {
        var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "anonymous";

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: tenantId,
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,               // 20 uploads per minute
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6           // 10-second segments
            });
    });

    // Token generation (prevent brute force): 10 tokens per hour
    options.AddPolicy("TokenGeneration", httpContext =>
    {
        var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "anonymous";

        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: tenantId,
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                ReplenishmentPeriod = TimeSpan.FromHours(1),
                TokensPerPeriod = 5,
                AutoReplenishment = true
            });
    });

    // Preview image access (prevent bulk harvesting): 60 per minute per IP
    options.AddPolicy("PreviewAccess", httpContext =>
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ipAddress,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,                // 60 requests per minute
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5
            });
    });

    // Anonymous .hmap contributions: 2 per hour per IP
    options.AddPolicy("HmapContribution", httpContext =>
    {
        // Check X-Forwarded-For header first (for reverse proxy scenarios)
        var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ipAddress = !string.IsNullOrEmpty(forwardedFor)
            ? forwardedFor.Split(',')[0].Trim()
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"contribute:{ipAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 2,
                Window = TimeSpan.FromHours(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // Global concurrency limit (1000 concurrent requests)
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        return RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: "global",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 2000,             // 1000 concurrent requests
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 1000
            });
    });

    // Custom rejection response
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;

        // Try to get retry-after metadata
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
            ? retryAfterValue.TotalSeconds
            : (double?)null;

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "RateLimitExceeded",
            message = "Too many requests. Please try again later.",
            retryAfter = retryAfter
        }, cancellationToken: token);

        // Log rate limit hit
        var tenantId = context.HttpContext.Items["TenantId"]?.ToString();
        if (tenantId != null)
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Rate limit exceeded for tenant {TenantId} on {Path}",
                tenantId, context.HttpContext.Request.Path);
        }
    };
});

// Add output caching for tile images
// This provides fast in-memory caching of tile responses, reducing disk I/O
builder.Services.AddOutputCache(options =>
{
    // Default cache policy for tiles: 60 seconds in-memory cache
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(60)));
});

// Configure forwarded headers for reverse proxy support (Caddy/nginx)
// Allows proper HTTPS detection and client IP forwarding when behind a proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust all proxies in container network (Caddy is our trusted proxy)
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add CORS only if explicitly enabled (disabled by default for security)
// When Web and API share the same domain (via reverse proxy), CORS is not needed
// Only enable if you need browser-based cross-origin API calls
var enableCors = builder.Configuration.GetValue<bool>("EnableCors", false);
if (enableCors)
{
    var allowedOrigins = builder.Configuration.GetSection("CorsAllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowWebFrontend", policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                // Production: restrict to specific origins
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            }
            else
            {
                // Development fallback: allow any origin (insecure, use only in dev)
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            }
        });
    });
}

// Configure JSON serialization for HTTP responses (SSE, API endpoints)
// Use camelCase to match JavaScript conventions - .NET JSInterop handles this automatically
// for Blazor components, but HTTP/SSE responses need explicit configuration
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Enable response compression (Brotli + Gzip) for API responses
// Note: SSE endpoint (/map/updates) is excluded via NoCompressionForContentTypeAttribute
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();

    // Exclude SSE content type from compression (breaks streaming)
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
        .Where(mimeType => mimeType != "text/event-stream");
});

var app = builder.Build();

// Ensure grid storage directory exists BEFORE database creation
Directory.CreateDirectory(gridStorage);
Directory.CreateDirectory(Path.Combine(gridStorage, "grids"));

// Diagnostic: log raw vs resolved storage and data protection path
app.Logger.LogInformation(
    "GridStorage (raw): {Raw} | GridStorage (resolved): {Resolved} | DataProtection: {DP}",
    configuredGridStorage ?? "(null)", gridStorage, dataProtectionPath);

// Apply EF Core migrations and seed database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();

    // Seed Identity roles and optional bootstrap admin
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // Seed Identity roles (including "Users" for admin-lite user management and "SuperAdmin" for multi-tenancy)
    var roles = new[] { "Admin", "SuperAdmin", "Users", "Writer", "Pointer", "Map", "Markers", "Upload" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            var result = await roleManager.CreateAsync(new IdentityRole(role));
            if (!result.Succeeded)
            {
                logger.LogError("Failed creating role {Role}: {Errors}", role, string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    // Ensure default tenant exists before bootstrapping admin
    const string defaultTenantId = "default-tenant-1";
    var defaultTenant = await db.Tenants.FindAsync(defaultTenantId);
    if (defaultTenant == null)
    {
        defaultTenant = new TenantEntity
        {
            Id = defaultTenantId,
            Name = defaultTenantId,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        db.Tenants.Add(defaultTenant);
        await db.SaveChangesAsync();
        logger.LogInformation("Created default tenant: {TenantId}", defaultTenantId);
    }

    var bootstrapEnabled = configuration.GetValue<bool>("BootstrapAdmin:Enabled");
    if (bootstrapEnabled)
    {
        var adminUser = configuration.GetValue<string>("BootstrapAdmin:Username") ?? "admin";
        var adminPass = configuration.GetValue<string>("BootstrapAdmin:Password") ?? "admin123!";

        var user = await userManager.FindByNameAsync(adminUser);
        if (user == null)
        {
            user = new IdentityUser { UserName = adminUser };
            var create = await userManager.CreateAsync(user, adminPass);
            if (!create.Succeeded)
            {
                logger.LogError("Failed creating bootstrap admin: {Errors}", string.Join(", ", create.Errors.Select(e => e.Description)));
            }
            else
            {
                var addRoles = await userManager.AddToRolesAsync(user, roles);
                if (!addRoles.Succeeded)
                {
                    logger.LogError("Failed assigning roles to bootstrap admin: {Errors}", string.Join(", ", addRoles.Errors.Select(e => e.Description)));
                }
                else
                {
                    // Link admin user to default tenant with TenantAdmin role
                    var tenantUser = new TenantUserEntity
                    {
                        TenantId = defaultTenantId,
                        UserId = user.Id,
                        Role = TenantRole.TenantAdmin,
                        JoinedAt = DateTime.UtcNow,
                        PendingApproval = false
                    };
                    db.TenantUsers.Add(tenantUser);
                    await db.SaveChangesAsync();

                    // Give admin all permissions
                    var permissions = new[] { Permission.Map, Permission.Markers, Permission.Pointer, Permission.Upload, Permission.Writer };
                    foreach (var permission in permissions)
                    {
                        db.TenantPermissions.Add(new TenantPermissionEntity
                        {
                            TenantUserId = tenantUser.Id,
                            Permission = permission
                        });
                    }
                    await db.SaveChangesAsync();

                    logger.LogWarning("Bootstrap admin created and linked to default tenant. CHANGE PASSWORD IMMEDIATELY.");
                }
            }
        }
        else
        {
            // Ensure existing admin is linked to default tenant
            var existingTenantUser = await db.TenantUsers
                .FirstOrDefaultAsync(tu => tu.UserId == user.Id && tu.TenantId == defaultTenantId);

            if (existingTenantUser == null)
            {
                var tenantUser = new TenantUserEntity
                {
                    TenantId = defaultTenantId,
                    UserId = user.Id,
                    Role = TenantRole.TenantAdmin,
                    JoinedAt = DateTime.UtcNow,
                    PendingApproval = false
                };
                db.TenantUsers.Add(tenantUser);
                await db.SaveChangesAsync();

                // Give admin all permissions
                var permissions = new[] { Permission.Map, Permission.Markers, Permission.Pointer, Permission.Upload, Permission.Writer };
                foreach (var permission in permissions)
                {
                    db.TenantPermissions.Add(new TenantPermissionEntity
                    {
                        TenantUserId = tenantUser.Id,
                        Permission = permission
                    });
                }
                await db.SaveChangesAsync();

                logger.LogInformation("Linked existing admin user to default tenant.");
            }
        }
    }

    // Run token migration to update existing tokens to tenant-prefixed format
    var tokenMigration = scope.ServiceProvider.GetRequiredService<TokenMigrationService>();
    await tokenMigration.MigrateTokensAsync();
}

// Configure the HTTP request pipeline

// Use forwarded headers early to ensure HTTPS scheme detection works
app.UseForwardedHeaders();

// Enable response compression (must be early in pipeline, before other middleware)
app.UseResponseCompression();

// Enable detailed exception page in Development
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSerilogRequestLogging(options =>
{
    // Suppress noisy 404 logs for missing tile images under /map/grids
    options.GetLevel = (httpContext, elapsedMs, ex) =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var status = httpContext.Response?.StatusCode ?? 200;

        if (path.StartsWith("/map/grids", StringComparison.OrdinalIgnoreCase) && status == StatusCodes.Status404NotFound)
            return LogEventLevel.Debug; // below normal min level, effectively hidden

        if (ex != null || status >= 500) return LogEventLevel.Error;
        if (status >= 400) return LogEventLevel.Warning;
        return LogEventLevel.Information;
    };
});

// Use CORS only if enabled (disabled by default for security)
if (app.Configuration.GetValue<bool>("EnableCors", false))
{
    app.UseCors("AllowWebFrontend");
}

// Diagnostic middleware for debugging client endpoint issues
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    // Log ALL /client/* requests to diagnose routing issues
    if (context.Request.Path.StartsWithSegments("/client"))
    {
        logger.LogWarning(
            "DIAGNOSTIC: Incoming request: {Method} {Path} from {IP}",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress);
    }

    await next();

    // Log response status for /client/* requests
    if (context.Request.Path.StartsWithSegments("/client"))
    {
        logger.LogWarning(
            "DIAGNOSTIC: Response: {Method} {Path} -> {StatusCode}",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode);
    }
});

// Enable rate limiting (must be before authentication)
// Protects endpoints from excessive concurrent requests
app.UseRateLimiter();

// Use ASP.NET Core authentication and authorization
app.UseAuthentication();

// Enable tenant context middleware (must be after authentication)
app.UseMiddleware<TenantContextMiddleware>();

app.UseAuthorization();

// Enable output caching for tile endpoints
app.UseOutputCache();

// Map endpoints
// Identity-based endpoints will replace legacy auth endpoints
// app.MapAuthEndpoints(); // legacy
app.MapIdentityEndpoints();
app.MapClientEndpoints();
app.MapMapEndpoints();
app.MapCustomMarkerEndpoints();
app.MapPollingEndpoints(); // Polling fallback for SSE (VPN users)
app.MapRoadEndpoints();
app.MapPingEndpoints();
app.MapNotificationEndpoints(); // Notification system endpoints
app.MapTimerEndpoints(); // Timer system endpoints
app.MapInvitationEndpoints();
app.MapTenantAdminEndpoints(); // Phase 5: Tenant admin endpoints (RBAC)
app.MapMapAdminEndpoints(); // Map admin endpoints (tenant-scoped map management)
app.MapSuperadminEndpoints(); // Phase 5: Superadmin endpoints (global tenant management)
app.MapAuditEndpoints(); // Phase 6: Audit log viewer endpoints
app.MapDatabaseEndpoints();
app.MapPublicMapEndpoints(); // Public map viewing (no auth required)
app.MapPublicContributionEndpoints(); // Public .hmap contributions (anonymous, rate limited)

// Public version endpoint - returns build information (no authentication required)
app.MapGet("/version", (IBuildInfoProvider buildInfo) => 
{
    return Results.Json(buildInfo.Get("api"));
}).AllowAnonymous();

// Handle stray Identity UI redirects that may appear in logs/tools
app.MapGet("/Account/Login", () => Results.Unauthorized()).DisableAntiforgery();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

app.Logger.LogInformation("HnH Mapper API started. Use Aspire dashboard to view endpoints.");

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
