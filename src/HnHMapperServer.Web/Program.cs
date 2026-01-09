using HnHMapperServer.Web.Components;
using HnHMapperServer.Web.Services;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Constants;
using MudBlazor.Services;
using MudBlazor;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Serilog;
using Microsoft.AspNetCore.Components.Server;
using Serilog.Events;
using SixLabors.ImageSharp;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for long-lived SignalR connections (Blazor Server)
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Disable MinResponseDataRate to prevent SignalR circuit timeouts
    // SignalR has its own keep-alive mechanism (default 15 seconds)
    serverOptions.Limits.MinResponseDataRate = null;

    // Allow large file uploads for .hmap import (up to 1GB)
    serverOptions.Limits.MaxRequestBodySize = 1024L * 1024 * 1024; // 1GB
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

// Add database context
builder.Services.AddDbContext<HnHMapperServer.Infrastructure.Data.ApplicationDbContext>(options =>
{
    var configuredGridStorage = builder.Configuration["GridStorage"]; // raw from config/env
    var gridStorage = configuredGridStorage;
    if (string.IsNullOrWhiteSpace(gridStorage))
    {
        // Default to shared solution-level path so Web and API use identical storage
        gridStorage = System.IO.Path.GetFullPath(System.IO.Path.Combine(builder.Environment.ContentRootPath, "..", "map"));
    }
    else if (!System.IO.Path.IsPathRooted(gridStorage))
    {
        // Resolve relative GridStorage consistently to solution-level path
        gridStorage = System.IO.Path.GetFullPath(System.IO.Path.Combine(builder.Environment.ContentRootPath, "..", gridStorage));
    }

    // Ensure the directory exists
    if (!System.IO.Directory.Exists(gridStorage))
    {
        System.IO.Directory.CreateDirectory(gridStorage);
    }

    var dbPath = System.IO.Path.Combine(gridStorage, "grids.db");
    var fullPath = System.IO.Path.GetFullPath(dbPath);

    // Diagnostic logging removed to reduce noise during tile serving
    // Database path and GridStorage were already logged at startup

    options.UseSqlite($"Data Source={fullPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True", sqliteOptions =>
    {
        sqliteOptions.CommandTimeout(30); // 30 second timeout
    });

    // Disable EF Core command logging completely
    options.ConfigureWarnings(warnings =>
        warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted));
});

// Register services
builder.Services.AddScoped<HnHMapperServer.Web.Services.MapDataService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.UserService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.SafeJsInterop>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.ReconnectionState>(); // Changed from Singleton to Scoped - each circuit needs its own instance to prevent cross-user event interference
builder.Services.AddSingleton<HnHMapperServer.Services.Interfaces.IBuildInfoProvider, HnHMapperServer.Services.Services.BuildInfoProvider>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.VersionClient>();

// Register multi-tenancy services
builder.Services.AddScoped<HnHMapperServer.Web.Services.TenantContextService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.ITenantService, HnHMapperServer.Web.Services.TenantService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.IInvitationService, HnHMapperServer.Web.Services.InvitationService>();

// Register Map feature services (scoped to Blazor circuit for per-user state)
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.CharacterTrackingService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.MarkerStateService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.CustomMarkerStateService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.MapNavigationService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.LayerVisibilityService>();

// Add HttpContextAccessor and auth state cache
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HnHMapperServer.Web.Services.AuthenticationStateCache>();

// Add circuit services accessor to capture authentication state when circuit starts
builder.Services.AddScoped<HnHMapperServer.Web.Services.CircuitServicesAccessor>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HnHMapperServer.Web.Services.CircuitServicesAccessor>(sp => sp.GetRequiredService<HnHMapperServer.Web.Services.CircuitServicesAccessor>());

// Add reconnection circuit handler to track SignalR connection state
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HnHMapperServer.Web.Security.ReconnectionCircuitHandler>();

// Configure forwarded headers for reverse proxy support (Caddy/nginx)
// Allows proper HTTPS detection and client IP forwarding when behind a proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust all proxies in container network (Caddy is our trusted proxy)
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure SignalR Hub options for Blazor Server
// This fixes "The maximum message size of 32768B was exceeded" errors
builder.Services.AddSignalR(options =>
{
    // Increase max message size for large file uploads (.hmap files can be 500MB+)
    // In Blazor Server, IBrowserFile streams go through SignalR
    options.MaximumReceiveMessageSize = 1024L * 1024 * 1024; // 1GB

    // Increase parallel invocations to handle multiple concurrent JS interop calls
    options.MaximumParallelInvocationsPerClient = 10; // Default is 1
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Temporarily enable in production for debugging circuit crashes
        options.DetailedErrors = true; // TODO: Change back to builder.Environment.IsDevelopment() after debugging
    });

// Enable detailed circuit errors only in development to diagnose UI/circuit termination issues
builder.Services.Configure<CircuitOptions>(options =>
{
    // Temporarily enable in production for debugging circuit crashes
    options.DetailedErrors = true; // TODO: Change back to builder.Environment.IsDevelopment() after debugging

    // Increase JS Interop timeout to allow for initial map initialization
    // Default is 1 minute which is too short for:
    // - Loading Leaflet library and plugins (now bundled locally, but adds safety margin)
    // - Initializing map with all layers and event handlers
    // - Network latency in production environments
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);

    // Keep disconnected circuits longer for better reconnection experience
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
});

// Add MudBlazor services
builder.Services.AddMudServices(options =>
{
    // Configure popover options for stable dialog positioning
    // ThrowOnDuplicateProvider=false allows nested providers in dialogs
    options.PopoverOptions.ThrowOnDuplicateProvider = false;
    
    // Reduce resize spam and suppress initial resize during reconnect
    options.ResizeOptions = new ResizeOptions
    {
        ReportRate = 250,
        SuppressInitEvent = true,
        NotifyOnBreakpointOnly = false
    };
});

// Add output caching for tile images
// This provides fast in-memory caching of tile responses, reducing disk I/O
builder.Services.AddOutputCache(options =>
{
    // Default cache policy for tiles: 60 seconds in-memory cache
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(60)));
});

// Configure shared data protection for cookie sharing with API
var gridStorageForDp = builder.Configuration["GridStorage"];
if (string.IsNullOrWhiteSpace(gridStorageForDp))
{
    gridStorageForDp = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "map"));
}
else if (!Path.IsPathRooted(gridStorageForDp))
{
    // Resolve relative GridStorage consistently to solution-level path
    gridStorageForDp = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", gridStorageForDp));
}
var dataProtectionPath = Path.Combine(gridStorageForDp, "DataProtection-Keys");

Directory.CreateDirectory(dataProtectionPath);

// Diagnostic: log DataProtection path
builder.Logging.AddConsole().Services.BuildServiceProvider()
    .GetRequiredService<ILogger<Program>>()
    .LogInformation("DataProtection: {DP}", dataProtectionPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("HnHMapper");

// Configure Cookie Authentication to match API cookie
// Use Identity.Application scheme so SignInManager works
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "HnH.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.LogoutPath = "/login";
        options.AccessDeniedPath = "/login";

        // Rebuild claims when security stamp or roles change without requiring manual logout
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async context =>
            {
                var services = context.HttpContext.RequestServices;
                var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
                var signInManager = services.GetRequiredService<SignInManager<IdentityUser>>();
                var identityOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value;

                var principal = context.Principal;
                if (principal?.Identity?.IsAuthenticated != true)
                {
                    return;
                }

                var user = await userManager.GetUserAsync(principal!);
                if (user == null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }

                // If stamp claim missing, allow but try to refresh silently
                var stampClaimType = identityOptions.ClaimsIdentity.SecurityStampClaimType;
                var principalStamp = principal.FindFirstValue(stampClaimType);
                var currentStamp = await userManager.GetSecurityStampAsync(user);

                var roles = await userManager.GetRolesAsync(user);
                var principalRoles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(x => x).ToArray();
                var rolesChanged = !roles.OrderBy(x => x).SequenceEqual(principalRoles);

                if (string.IsNullOrEmpty(principalStamp) || principalStamp != currentStamp || rolesChanged)
                {
                    // Build fresh principal (includes custom auth claims via ClaimsPrincipalFactory)
                    var newPrincipal = await signInManager.CreateUserPrincipalAsync(user);
                    context.ReplacePrincipal(newPrincipal);
                    context.ShouldRenew = true;
                }
            }
        };
    });

// Add IdentityCore for credential validation against shared DB
builder.Services
    .AddIdentityCore<IdentityUser>(options =>
    {
        // Password policy: 6+ characters minimum (same as API)
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequiredLength = 6;
    })
    .AddRoles<IdentityRole>()
    .AddSignInManager() // Required for security stamp validation
    .AddClaimsPrincipalFactory<HnHMapperServer.Web.Security.TenantClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<HnHMapperServer.Infrastructure.Data.ApplicationDbContext>()
    .AddDefaultTokenProviders();

// Configure security stamp validation interval for fast role/permission updates
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromSeconds(10);
});

// Add authorization services
builder.Services.AddAuthorization();

// Add revalidating authentication state provider for Blazor (checks security stamps)
builder.Services.AddScoped<AuthenticationStateProvider, HnHMapperServer.Web.Services.RevalidatingIdentityAuthenticationStateProvider<IdentityUser>>();

// Add authentication delegating handler for API calls
builder.Services.AddTransient<HnHMapperServer.Web.Services.AuthenticationDelegatingHandler>();

// Add HttpClient for API calls with proper authentication forwarding
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    // Default to Aspire service discovery in development, Docker Compose HTTP in production
    // Override with ApiBaseUrl env var: "http://api:8080" for Docker, "https://api" for Aspire
    apiBaseUrl = builder.Environment.IsDevelopment() ? "https://api" : "http://api:8080";
}

// Diagnostic: log the API base URL resolved for the named client
builder.Logging.AddConsole().Services.BuildServiceProvider()
    .GetRequiredService<ILogger<Program>>()
    .LogInformation("API HttpClient BaseAddress: {ApiBaseUrl}", apiBaseUrl);

// Standard API client WITH resilience (retries, circuit breaker, timeouts)
// Used for regular API calls that don't involve streaming uploads
builder.Services.AddHttpClient("API", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
})
.AddHttpMessageHandler<HnHMapperServer.Web.Services.AuthenticationDelegatingHandler>()
.AddStandardResilienceHandler();

// File upload client WITHOUT resilience - streams cannot be retried
// Used only for .hmap imports and other large file uploads
builder.Services.AddHttpClient("APIUpload", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(45);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
})
.AddHttpMessageHandler<HnHMapperServer.Web.Services.AuthenticationDelegatingHandler>();

// Add cascading authentication state
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();
// Diagnostics: echo environment-driven paths and API base
{
    var raw = app.Configuration["GridStorage"];
    var resolved = raw;
    if (string.IsNullOrWhiteSpace(resolved))
        resolved = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "map"));
    else if (!Path.IsPathRooted(resolved))
        resolved = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", resolved));
    var dp = Path.Combine(resolved, "DataProtection-Keys");
    var apiBaseUrlDiag = app.Configuration["ApiBaseUrl"];
    if (string.IsNullOrWhiteSpace(apiBaseUrlDiag)) apiBaseUrlDiag = "https://api";
    app.Logger.LogInformation("GridStorage (raw): {Raw} | GridStorage (resolved): {Resolved} | DataProtection: {DP} | API Base: {Api}", raw ?? "(null)", resolved, dp, apiBaseUrlDiag);
}

// Configure the HTTP request pipeline.

// Use forwarded headers early to ensure HTTPS scheme detection works
app.UseForwardedHeaders();

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Only enable HTTPS redirection when explicitly configured (e.g., when behind HTTPS-enabled reverse proxy)
// For IP-only HTTP deployment, leave this disabled (default: false)
// Enable with environment variable: EnableHttpsRedirect=true
if (app.Configuration.GetValue<bool>("EnableHttpsRedirect", false))
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

// Use authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Enable output caching for tile endpoints
app.UseOutputCache();

// SSE proxy endpoint - forwards browser EventSource requests to API service
// This is needed because browsers can't use Aspire service discovery
app.MapGet("/map/updates", async (HttpContext context, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    logger.LogWarning("[SSE Proxy] Request received from browser");
    
    if (!context.User.Identity?.IsAuthenticated ?? true)
    {
        logger.LogError("[SSE Proxy] User not authenticated");
        return Results.Unauthorized();
    }

    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
    {
        logger.LogError("[SSE Proxy] User lacks Map permission");
        return Results.Unauthorized();
    }

    logger.LogWarning("[SSE Proxy] Auth passed, forwarding to API service...");

    try
    {
        // Create HTTP client to API service
        var apiClient = httpClientFactory.CreateClient("API");
        apiClient.Timeout = Timeout.InfiniteTimeSpan;

    // Forward request to API service.
    //
    // IMPORTANT:
    // The browser SSE client may connect using query parameters (e.g. `?since=<token>`) to avoid
    // re-downloading and re-parsing the entire initial tile cache on reconnect.
    //
    // If we drop the query string here, the API always thinks `since=0` and will resend the full
    // tile cache snapshot (potentially ~300k tiles), which can freeze the browser for tens of seconds.
    //
    // Therefore we MUST forward the query string verbatim.
    var requestUri = "/map/updates" + (context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty);
    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("Accept", "text/event-stream");

    logger.LogWarning("[SSE Proxy] Sending request to API: {RequestUri}", requestUri);
        var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        
        logger.LogWarning("[SSE Proxy] API response status: {StatusCode}", response.StatusCode);
        
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("[SSE Proxy] API returned error: {StatusCode}", response.StatusCode);
            return Results.StatusCode((int)response.StatusCode);
        }

        // Set SSE headers before writing anything
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        
        logger.LogWarning("[SSE Proxy] Starting to stream response from API to browser");

        // Stream response from API to browser with buffering disabled
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        
        var buffer = new byte[4096];
        int bytesRead;
        
        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        
        logger.LogWarning("[SSE Proxy] Stream ended");
        return Results.Empty;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SSE Proxy] Exception while proxying SSE");
        return Results.StatusCode(500);
    }
}).RequireAuthorization();

// Polling proxy endpoint - forwards poll requests to API service (fallback for SSE)
// This is needed for VPN users where SSE connections fail
app.MapGet("/map/api/v1/poll", async (HttpContext context, IHttpClientFactory httpClientFactory, [FromQuery] long? since) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
        return Results.Unauthorized();

    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
        return Results.Unauthorized();

    try
    {
        var apiClient = httpClientFactory.CreateClient("API");
        var requestUri = since.HasValue ? $"/map/api/v1/poll?since={since}" : "/map/api/v1/poll";

        // Forward auth cookie to API
        if (context.Request.Headers.TryGetValue("Cookie", out var cookie))
        {
            apiClient.DefaultRequestHeaders.Add("Cookie", cookie.ToString());
        }

        var response = await apiClient.GetAsync(requestUri, context.RequestAborted);

        if (!response.IsSuccessStatusCode)
            return Results.StatusCode((int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync(context.RequestAborted);
        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "[Poll Proxy] Exception while proxying poll request");
        return Results.StatusCode(500);
    }
}).RequireAuthorization();

// Grid IDs endpoint - queries database directly (like tile serving)
app.MapGet("/map/api/v1/grids", async (
    HttpContext context,
    [FromQuery] int mapId,
    [FromQuery] int minX,
    [FromQuery] int maxX,
    [FromQuery] int minY,
    [FromQuery] int maxY,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db,
    ILogger<Program> logger) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
        return Results.Unauthorized();

    // Extract tenant ID from claims (CRITICAL for global query filters to work)
    var tenantId = context.User.FindFirst("TenantId")?.Value;
    if (string.IsNullOrEmpty(tenantId))
        return Results.Unauthorized();

    // Store in context for ITenantContextAccessor (used by EF Core global query filters)
    context.Items["TenantId"] = tenantId;

    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
        return Results.Unauthorized();

    // Limit bounds to prevent excessive queries
    var maxRange = 50;
    if (maxX - minX > maxRange || maxY - minY > maxRange)
        return Results.BadRequest($"Coordinate range too large. Maximum {maxRange} tiles per dimension.");

    try
    {
        var grids = await db.Grids
            .AsNoTracking()
            .Where(g => g.Map == mapId &&
                       g.CoordX >= minX && g.CoordX <= maxX &&
                       g.CoordY >= minY && g.CoordY <= maxY)
            .Select(g => new { x = g.CoordX, y = g.CoordY, gridId = g.Id })
            .ToListAsync();

        return Results.Json(grids);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error fetching grid IDs for map {MapId}", mapId);
        return Results.StatusCode(500);
    }
}).RequireAuthorization();

// Single tile info endpoint - returns grid ID for a specific tile
// NOTE: Using /api/tile-info instead of /map/api/v1/grid because Caddy routes /map/api/* to API service
app.MapGet("/api/tile-info", async (
    HttpContext context,
    [FromQuery] int mapId,
    [FromQuery] int x,
    [FromQuery] int y,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db,
    ILogger<Program> logger) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
        return Results.Unauthorized();

    var tenantId = context.User.FindFirst("TenantId")?.Value;
    if (string.IsNullOrEmpty(tenantId))
        return Results.Unauthorized();

    context.Items["TenantId"] = tenantId;

    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
        return Results.Unauthorized();

    try
    {
        logger.LogInformation("Fetching grid info for map {MapId} at ({X}, {Y}), tenant: {TenantId}",
            mapId, x, y, tenantId);

        var grid = await db.Grids
            .AsNoTracking()
            .Where(g => g.Map == mapId && g.CoordX == x && g.CoordY == y)
            .Select(g => new {
                x = g.CoordX,
                y = g.CoordY,
                gridId = g.Id,
                mapId = g.Map,
                nextUpdate = g.NextUpdate
            })
            .FirstOrDefaultAsync();

        logger.LogInformation("Grid query result: {Result}", grid != null ? $"Found gridId={grid.gridId}" : "Not found");

        if (grid == null)
            return Results.NotFound();

        return Results.Json(grid);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error fetching grid info for map {MapId} at ({X}, {Y})", mapId, x, y);
        return Results.StatusCode(500);
    }
}).RequireAuthorization();

// Tile serving endpoint - MUST be before MapRazorComponents to avoid routing conflicts
// DB-first lookup to support zoom 0 tiles stored under grids/{gridId}.png
app.MapGet("/map/grids/{**path}", async (
    HttpContext context,
    string path,
    IConfiguration configuration,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
        return Results.Unauthorized();

    // Extract tenant ID from claims (CRITICAL for global query filters to work)
    var tenantId = context.User.FindFirst("TenantId")?.Value;
    if (string.IsNullOrEmpty(tenantId))
        return Results.Unauthorized();

    // Store in context for ITenantContextAccessor (used by EF Core global query filters)
    context.Items["TenantId"] = tenantId;

    // Check for Map permission claim
    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
        return Results.Unauthorized();

    // Parse path: {mapId}/{zoom}/{x}_{y}.png
    var parts = path.Split('/');
    if (parts.Length != 3)
        return Results.NotFound();

    if (!int.TryParse(parts[0], out var mapId))
        return Results.NotFound();

    if (!int.TryParse(parts[1], out var zoom))
        return Results.NotFound();

    var coordPart = parts[2].Replace(".png", "");
    var coords = coordPart.Split('_');
    if (coords.Length != 2)
        return Results.NotFound();

    if (!int.TryParse(coords[0], out var x))
        return Results.NotFound();

    if (!int.TryParse(coords[1], out var y))
        return Results.NotFound();

    // Get GridStorage from configuration (use raw value to match API behavior)
    var gridStorage = configuration["GridStorage"] ?? "map";

    string? filePath = null;

    // Performance optimization: only query DB for zoom 0 tiles (which may be stored under grids/{gridId}.png)
    // For zoom >= 1, tiles are always in the standard {mapId}/{zoom}/{x}_{y}.png structure
    if (zoom == 0)
    {
        // 1) DB-first lookup: covers zoom 0 tiles stored under grids/{gridId}.png
        // NOTE: Global query filter automatically filters by tenantId from HttpContext.Items["TenantId"]
        var tile = await db.Tiles
            .Where(t => t.MapId == mapId && t.Zoom == zoom && t.CoordX == x && t.CoordY == y)
            .FirstOrDefaultAsync();

        if (tile != null)
        {
            // SECURITY: Verify tile belongs to current tenant (defense-in-depth)
            if (tile.TenantId != tenantId)
            {
                return Results.Unauthorized();
            }

            if (!string.IsNullOrEmpty(tile.File))
            {
                // Tile found in database - use stored file path (relative to gridStorage)
                filePath = Path.Combine(gridStorage, tile.File);
            }
        }
        else
        {
            // Fallback to direct file system lookup for zoom 0
            // Tenant-specific path: tenants/{tenantId}/{mapId}/{zoom}/{x}_{y}.png
            var tenantPath = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{x}_{y}.png");
            if (File.Exists(tenantPath))
            {
                filePath = tenantPath;
            }
        }
    }
    else
    {
        // 2) For zoom >= 1, skip DB query and use tenant-specific path
        // Tenant-specific path: tenants/{tenantId}/{mapId}/{zoom}/{x}_{y}.png
        var directPath = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{x}_{y}.png");
        if (File.Exists(directPath))
        {
            filePath = directPath;
        }
    }
    
    if (filePath == null || !File.Exists(filePath))
    {
        // Check if we should return a transparent PNG instead of 404 (reduces browser console noise)
        var returnTransparentTile = configuration.GetValue<bool>("ReturnTransparentTilesOnMissing", false);
        
        if (returnTransparentTile)
        {
            // Return a minimal 1x1 transparent PNG (smallest valid PNG: 67 bytes)
            // This eliminates browser console 404 errors while maintaining cache benefits
            var transparentPng = new byte[] {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1 dimensions
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT chunk
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, // compressed data
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND chunk
                0x42, 0x60, 0x82
            };
            
            context.Response.Headers.Append("Cache-Control", "public, max-age=300, stale-while-revalidate=60");
            context.Response.ContentType = "image/png";
            return Results.Bytes(transparentPng, "image/png");
        }
        else
        {
            // Standard 404 response with long cache to reduce repeated requests over unmapped areas (5 minutes)
            context.Response.Headers.Append("Cache-Control", "public, max-age=300, stale-while-revalidate=60");
            return Results.NotFound();
        }
    }

    // Long-lived cache for tile hits (1 year) - tiles are immutable once created
    // Public caching is safe here as tiles are revision-controlled via ?v= query param
    context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
    return Results.File(filePath, "image/png");
}).RequireAuthorization()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromSeconds(60))  // In-memory cache for 60 seconds
      .SetVaryByQuery("v", "cache")      // Vary by revision and cache-bust params
      .SetVaryByRouteValue("path")       // Vary by tile path (mapId/zoom/x_y)
      .Tag("tiles"));                     // Tag for bulk invalidation if needed

// Public map proxy endpoints - forwards requests to API service
// These endpoints don't require authentication (public maps are unauthenticated)
app.MapGet("/public/{slug}/tiles/{**path}", async (
    HttpContext context,
    string slug,
    string path,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    try
    {
        var apiClient = httpClientFactory.CreateClient("API");
        var requestUri = $"/public/{slug}/tiles/{path}";

        // Forward any query string (e.g., cache busting parameters)
        if (context.Request.QueryString.HasValue)
            requestUri += context.Request.QueryString.Value;

        var response = await apiClient.GetAsync(requestUri, context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            // Forward cache headers even for 404s
            if (response.Headers.TryGetValues("Cache-Control", out var cacheValues))
                context.Response.Headers.CacheControl = string.Join(", ", cacheValues);

            return Results.StatusCode((int)response.StatusCode);
        }

        // Forward response headers
        if (response.Headers.TryGetValues("Cache-Control", out var cacheControl))
            context.Response.Headers.CacheControl = string.Join(", ", cacheControl);
        if (response.Headers.TryGetValues("ETag", out var etag))
            context.Response.Headers.ETag = string.Join(", ", etag);
        if (response.Content.Headers.TryGetValues("Last-Modified", out var lastModified))
            context.Response.Headers["Last-Modified"] = string.Join(", ", lastModified);

        var content = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        return Results.File(content, "image/png");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Public Tile Proxy] Error proxying tile request for {Slug}/{Path}", slug, path);
        return Results.StatusCode(500);
    }
}).AllowAnonymous()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromSeconds(60))
      .SetVaryByRouteValue("slug", "path")
      .SetVaryByQuery("v")
      .Tag("public-tiles"));

// Public map info proxy - forwards map info requests to API service
app.MapGet("/public/{slug}/info", async (
    HttpContext context,
    string slug,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    try
    {
        var apiClient = httpClientFactory.CreateClient("API");
        var response = await apiClient.GetAsync($"/public/{slug}/info", context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return Results.Content(errorContent, "application/json", statusCode: (int)response.StatusCode);
        }

        var content = await response.Content.ReadAsStringAsync(context.RequestAborted);
        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Public Map Proxy] Error proxying info request for {Slug}", slug);
        return Results.StatusCode(500);
    }
}).AllowAnonymous()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromSeconds(60))
      .SetVaryByRouteValue("slug"));

// Public maps list proxy - forwards list request to API service
app.MapGet("/public/", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    try
    {
        var apiClient = httpClientFactory.CreateClient("API");
        var response = await apiClient.GetAsync("/public/", context.RequestAborted);

        if (!response.IsSuccessStatusCode)
            return Results.StatusCode((int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync(context.RequestAborted);
        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Public Map Proxy] Error proxying list request");
        return Results.StatusCode(500);
    }
}).AllowAnonymous()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromSeconds(60)));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Convenience redirect for any stray Identity UI redirects
app.MapGet("/Account/Login", () => Results.Redirect("/login")).DisableAntiforgery();

// Local login endpoint (validates via Identity, signs shared cookie for Web domain)
app.MapPost("/api/login", async (
    HttpContext context,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db) =>
{
    string username = string.Empty;
    string password = string.Empty;
    try
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            username = form["user"].ToString();
            if (string.IsNullOrEmpty(username)) username = form["username"].ToString();
            password = form["pass"].ToString();
            if (string.IsNullOrEmpty(password)) password = form["password"].ToString();
        }
        else
        {
            var body = await context.Request.ReadFromJsonAsync<LoginPayload>();
            username = body?.Username ?? string.Empty;
            password = body?.Password ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/login?error=1");

        var user = await userManager.FindByNameAsync(username);
        if (user == null) return Results.Redirect("/login?error=1");
        var valid = await userManager.CheckPasswordAsync(user, password);
        if (!valid) return Results.Redirect("/login?error=1");

        // Check if user has tenant assignment
        var hasTenant = await db.TenantUsers
            .IgnoreQueryFilters()
            .AnyAsync(tu => tu.UserId == user.Id && tu.JoinedAt != default);

        if (!hasTenant)
            return Results.Redirect("/login?error=no_tenant");

        // SignInManager will use TenantClaimsPrincipalFactory to add tenant claims automatically
        await signInManager.SignInAsync(user, isPersistent: true);

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("User {Username} logged in successfully", username);

        return Results.LocalRedirect("/");
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Login failed for user {Username}", username);
        return Results.Redirect("/login?error=1");
    }
}).DisableAntiforgery();

// Support both GET and POST for logout (GET for navigation, POST for form submission)
app.MapGet("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
    return Results.Redirect("/login");
});

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

// Payload model for JSON login
file sealed class LoginPayload
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

 
