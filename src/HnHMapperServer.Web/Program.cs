using HnHMapperServer.Web.Components;
using HnHMapperServer.Web.Services;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Infrastructure.Identity;
using MudBlazor.Services;
using MudBlazor;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using Serilog;
using Microsoft.AspNetCore.Components.Server;
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

// ImageSharp settings for tile generation.
// MaxDegreeOfParallelism caps the threads ONE ImageSharp operation (Resize, DrawImage, encode)
// uses for its row loop; it does not limit how many tiles are generated concurrently.
SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism = 2;

// ImageSharp 3.x serves every buffer up to 1 MiB from ArrayPool<byte>.Shared, rounded up to a
// power of two, and only larger buffers from its own unmanaged pool - the pool that
// MaximumPoolSizeMegabytes caps. A 400x400 Rgba32 tile frame (640,000 B) is therefore a 1 MiB
// array on the managed large object heap, kept by the shared pool after use (up to 32 per size
// per CPU partition, trimmed slowly on gen2 GCs) and bounded by neither setting; a 100x100 base
// tile (40,000 B -> 64 KiB) stays on the small-object heap. AllocationLimitMegabytes caps a
// single allocation (one image buffer), a backstop against a pathological image - it is not a
// running total.
SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
    SixLabors.ImageSharp.Memory.MemoryAllocator.Create(new SixLabors.ImageSharp.Memory.MemoryAllocatorOptions
    {
        MaximumPoolSizeMegabytes = 32,
        AllocationLimitMegabytes = 256
    });

// Configure Serilog. Serilog:MinimumLevel is the ONLY level gate for Console, File and OTLP
// (writeToProviders + ClearProviders make Logging:LogLevel inert) - see CLAUDE.md "Logging levels".
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    // LAST: FromLogContext + the root enricher that masks invite codes, token-revoke paths and client tokens in
    // every property for all sinks (ServiceDefaults; a test checks this call stays last).
    .ApplyServiceDefaults(),
    writeToProviders: true);

builder.Logging.ClearProviders();

// Add Aspire service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

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
// Publish the resolved path: every service reads configuration["GridStorage"], and a relative value
// would otherwise resolve against the working directory instead of the folder the database is in.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = gridStorage });

// Add database context
builder.Services.AddDbContext<HnHMapperServer.Infrastructure.Data.ApplicationDbContext>(options =>
{
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

// Cross-circuit cache of the cookbook's flat "All recipes" rows (~50k rows per tenant)
builder.Services.AddSingleton<HnHMapperServer.Web.Services.CookbookFlatCache>();

// Which game icons ship under wwwroot/gfx, so FoodIcons sends unshipped ones straight to the
// game's renderer instead of paying a local 404 first. wwwroot is immutable in the image.
builder.Services.AddSingleton(HnHMapperServer.Services.Services.ShippedIconScanner.Scan(builder.Environment.WebRootPath));

// Register public tile cache services for in-memory tile serving
builder.Services.AddSingleton<HnHMapperServer.Web.Services.PublicTileCacheService>();
builder.Services.AddHostedService<HnHMapperServer.Web.Services.PublicTileCacheHostedService>();

// Register LargeTileService for WebP tile generation (400x400 tiles)
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.ILargeTileService, HnHMapperServer.Services.Services.LargeTileService>();

// Register multi-tenancy services
builder.Services.AddScoped<HnHMapperServer.Web.Services.TenantContextService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.ITenantService, HnHMapperServer.Web.Services.TenantService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.IInvitationService, HnHMapperServer.Web.Services.InvitationService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.ClipboardService>();

// Register Map feature services (scoped to Blazor circuit for per-user state)
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.CharacterTrackingService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.MarkerStateService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.CustomMarkerStateService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.MapNavigationService>();
builder.Services.AddScoped<HnHMapperServer.Web.Services.Map.LayerVisibilityService>();

// Add HttpContextAccessor and the per-circuit auth state cache
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HnHMapperServer.Web.Services.AuthenticationStateCache>();

// Circuit services for code outside the circuit's DI scope: AuthenticationDelegatingHandler runs in
// IHttpClientFactory's handler scope (shared by all users) and reads the sending circuit's auth state
// through this accessor. Its state is a static AsyncLocal, so a singleton is correct.
builder.Services.AddSingleton<HnHMapperServer.Web.Services.CircuitServicesAccessor>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HnHMapperServer.Web.Services.ServicesAccessorCircuitHandler>();

// Captures the circuit's auth cookie and browser origin on every (re)connect
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HnHMapperServer.Web.Services.AuthenticationCaptureCircuitHandler>();

// Add reconnection circuit handler to track SignalR connection state
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HnHMapperServer.Web.Security.ReconnectionCircuitHandler>();

// Configure forwarded headers for reverse proxy support (Caddy/nginx)
// Allows proper HTTPS detection and client IP forwarding when behind a proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust all proxies in container network (Caddy is our trusted proxy)
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure SignalR Hub options for Blazor Server
// This fixes "The maximum message size of 32768B was exceeded" errors
builder.Services.AddSignalR(options =>
{
    // Increase max message size for large file uploads (.hmap files can be 500MB+)
    // In Blazor Server, IBrowserFile streams go through SignalR
    options.MaximumReceiveMessageSize = 1024L * 1024 * 1024; // 1GB

    // MaximumParallelInvocationsPerClient is deliberately NOT raised here. Blazor requires
    // the default of 1: "Blazor relies on MaximumParallelInvocationsPerClient set to 1,
    // which is the default value" - raising it breaks IBrowserFile uploads
    // (dotnet/aspnetcore#53951), which is exactly what this app does for .hmap imports.
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

builder.Services.Configure<CircuitOptions>(options =>
{
    // Detailed errors leak internals and keep extra per-circuit state; development only.
    options.DetailedErrors = builder.Environment.IsDevelopment();

    // Upper bound for every .NET-to-JS call made without its own CancellationToken - e.g.
    // downloadFileFromStream for the cookbook export, which can legitimately run long. Map
    // initialization does not depend on it: initializeMap creates Leaflet synchronously (the old
    // requestAnimationFrame wait, which never ran in a hidden tab, was what hit this limit).
    // A call or reply lost with a dropped connection surfaces as TaskCanceledException after
    // this long; Blazor does not replay JS interop after a reconnect.
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);

    // Keep disconnected circuits long enough to survive a flaky connection, but bound how
    // many we hold: every retained circuit keeps its component state (and, on /cookbook,
    // references into the shared flat-row cache) alive until a gen2 collection. Default is
    // 100 retained circuits - far more than this deployment ever has live users.
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.DisconnectedCircuitMaxRetained = 20;

    // .NET 10 turns circuit state persistence ON automatically with
    // AddInteractiveServerComponents(), defaulting to 1000 persisted circuits kept for
    // 2 hours in MemoryCache. That is a second retention tier behind the disconnected
    // pool above, and nothing in this app opted into it. Bound it to something that
    // matches real usage; state itself is small (no [PersistentState] properties yet),
    // but the entries are pure overhead at those defaults.
    options.PersistedCircuitInMemoryMaxRetained = 50;
    options.PersistedCircuitInMemoryRetentionPeriod = TimeSpan.FromMinutes(30);
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

    // Snackbar defaults. These used to be set as attributes on <MudSnackbarProvider>,
    // where they were never parameters - MudBlazor swallowed them into UserAttributes,
    // so the whole configuration was silently inert and the library defaults applied.
    // SnackbarConfiguration is where they actually live.
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomLeft;
    options.SnackbarConfiguration.MaxDisplayedSnackbars = 5;   // same as the default
    options.SnackbarConfiguration.PreventDuplicates = true;    // same as the default
    options.SnackbarConfiguration.HideTransitionDuration = 400; // default 2000; snappier dismissal

    // NOT ported: the old markup also carried VisibleStateDuration="400". That is how long a
    // snackbar stays readable (default 5000ms), not a transition timing - it was almost certainly
    // meant as a twin of HideTransitionDuration above. Since the attribute never took effect, every
    // toast this app has ever shown used the 5000ms default; honouring 400ms now would make them
    // flash past unread. Left at the default deliberately - set it here if a shorter dwell is wanted.
});

// Add output caching for tile images
// This provides fast in-memory caching of tile responses, reducing disk I/O
builder.Services.AddOutputCache(options =>
{
    // Default cache policy for tiles: 60 seconds in-memory cache
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(60)));
});

// Configure shared data protection for cookie sharing with API
var dataProtectionPath = Path.Combine(gridStorage, "DataProtection-Keys");

Directory.CreateDirectory(dataProtectionPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("HnHMapper");

// Configure Cookie Authentication to match API cookie
// Use Identity.Application scheme so SignInManager works
var authBuilder = builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
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
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var signInManager = services.GetRequiredService<SignInManager<ApplicationUser>>();
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
                // The cookie also carries the active tenant's role as a Role claim, so compare only the
                // Identity roles (e.g. SuperAdmin) - the tenant role is covered by the active-tenant check below.
                var tenantRoleClaim = principal.FindFirstValue(AuthorizationConstants.ClaimTypes.TenantRole);
                var principalRoles = principal.FindAll(ClaimTypes.Role)
                    .Select(c => c.Value)
                    .Where(r => !string.Equals(r, tenantRoleClaim, StringComparison.Ordinal))
                    .OrderBy(x => x).ToArray();
                var rolesChanged = !roles.OrderBy(x => x).SequenceEqual(principalRoles);

                // Active tenant changed (switched on this or another device, membership removed, tenant
                // suspended) -> rebuild so the cookie follows the persisted selection without a stamp bump.
                var db = services.GetRequiredService<HnHMapperServer.Infrastructure.Data.ApplicationDbContext>();
                var expectedTenantId = await HnHMapperServer.Infrastructure.Identity.ActiveTenantMembershipResolver
                    .ResolveTenantIdAsync(db, user.Id, user.ActiveTenantId);
                var cookieTenantId = principal.FindFirstValue(AuthorizationConstants.ClaimTypes.TenantId);
                var tenantChanged = !string.Equals(expectedTenantId, cookieTenantId, StringComparison.Ordinal);

                if (string.IsNullOrEmpty(principalStamp) || principalStamp != currentStamp || rolesChanged || tenantChanged)
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
    .AddIdentityCore<ApplicationUser>(options =>
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
    .AddClaimsPrincipalFactory<HnHMapperServer.Infrastructure.Identity.TenantClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<HnHMapperServer.Infrastructure.Data.ApplicationDbContext>()
    .AddDefaultTokenProviders();

// Configure security stamp validation interval for fast role/permission updates
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromSeconds(10);
});

// ---------------------------------------------------------------------------------------------
// External sign-in: Steam (OpenID 2.0) and Discord (OAuth2). Both handlers are registered here once; whether
// a scheme is LIVE, and which key/secret it uses, is decided by the superadmin at runtime
// (SuperAdmin → Sign-in & onboarding, stored in the database). DynamicAuthSchemeManager adds/removes the
// schemes and DynamicAuthOptionsConfigurator injects the stored credentials whenever the options are rebuilt.
// Deployment configuration (Authentication:Steam:*, Authentication:Discord:*) only seeds the initial values.
// ---------------------------------------------------------------------------------------------
authBuilder.AddCookie(IdentityConstants.ExternalScheme, options =>
{
    // Short-lived cookie that carries the provider's identity between the callback and our own sign-in
    options.Cookie.Name = "HnH.External";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
    options.SlidingExpiration = false;
});

authBuilder.AddSteam(options =>
{
    options.SignInScheme = IdentityConstants.ExternalScheme;
    // Lax (not the handler default None): None is rejected by browsers without Secure, i.e. on plain HTTP;
    // Lax still rides the top-level GET that Steam redirects back with.
    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

authBuilder.AddDiscord(options =>
{
    options.SignInScheme = IdentityConstants.ExternalScheme;
    options.Scope.Clear();
    options.Scope.Add("identify");          // no email scope: we never collect e-mail addresses
    options.UsePkce = true;
    options.SaveTokens = false;             // provider tokens are never persisted
    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Runtime credentials + scheme toggling (registered AFTER AddSteam/AddDiscord so the configurator runs last)
builder.Services.AddSingleton<HnHMapperServer.Services.Interfaces.AuthSettingsCache>();
builder.Services.AddSingleton<IConfigureOptions<AspNet.Security.OpenId.Steam.SteamAuthenticationOptions>, HnHMapperServer.Web.Security.DynamicAuthOptionsConfigurator>();
builder.Services.AddSingleton<IConfigureOptions<AspNet.Security.OAuth.Discord.DiscordAuthenticationOptions>, HnHMapperServer.Web.Security.DynamicAuthOptionsConfigurator>();
builder.Services.AddSingleton<HnHMapperServer.Web.Security.DynamicAuthSchemeManager>();
builder.Services.AddSingleton<HnHMapperServer.Web.Security.ExternalAuthProviders>();

// Services the external callback and the settings page need IN-PROCESS (no HnH.Auth cookie exists during the
// callback, so it cannot go through the API; the settings must be applied in the process hosting the handlers)
builder.Services.AddScoped<HnHMapperServer.Core.Interfaces.ITenantInvitationRepository, HnHMapperServer.Infrastructure.Repositories.TenantInvitationRepository>();
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.IAuditService, HnHMapperServer.Services.Services.AuditService>();
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.IInvitationService, HnHMapperServer.Services.Services.InvitationService>();
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.ITenantMembershipService, HnHMapperServer.Services.Services.TenantMembershipService>();
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.IExternalUserProvisioner, HnHMapperServer.Services.Services.ExternalUserProvisioner>();
// The Web process is the ONLY one that decrypts the provider secrets (it configures the handlers)
builder.Services.AddSingleton(new HnHMapperServer.Services.Interfaces.AuthSettingsStoreOptions { DecryptSecrets = true });
builder.Services.AddScoped<HnHMapperServer.Services.Interfaces.IAuthSettingsStore, HnHMapperServer.Services.Services.AuthSettingsStore>();

// Add authorization services
builder.Services.AddAuthorization();

// Add revalidating authentication state provider for Blazor (checks security stamps)
builder.Services.AddScoped<AuthenticationStateProvider, HnHMapperServer.Web.Services.RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

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
    UseCookies = false,
    // This client also pulls the bulk cookbook variation list: ~36 MB of JSON that gzips
    // to ~3.4 MB (measured). Without this the Web hop moves the whole uncompressed body
    // through large-object-heap buffers on every cache refresh.
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
})
.AddHttpMessageHandler<HnHMapperServer.Web.Services.AuthenticationDelegatingHandler>();

// Add cascading authentication state
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Sign-in settings: load the superadmin's saved state and bring the Steam/Discord schemes to life (or not)
{
    using var scope = app.Services.CreateScope();
    var settingsStore = scope.ServiceProvider.GetRequiredService<HnHMapperServer.Services.Interfaces.IAuthSettingsStore>();
    await settingsStore.WarmAsync();
    var settingsCache = app.Services.GetRequiredService<HnHMapperServer.Services.Interfaces.AuthSettingsCache>();
    app.Services.GetRequiredService<HnHMapperServer.Web.Security.DynamicAuthSchemeManager>().Start(settingsCache.Current!);
}

// Diagnostics: echo environment-driven paths and API base
{
    app.Logger.LogInformation("GridStorage (raw): {Raw} | GridStorage (resolved): {Resolved} | DataProtection: {DP} | API Base: {Api}", configuredGridStorage ?? "(null)", gridStorage, dataProtectionPath, apiBaseUrl);

    var shippedIcons = app.Services.GetRequiredService<HnHMapperServer.Core.Cookbook.ShippedIcons>();
    if (shippedIcons.IsKnown)
        app.Logger.LogInformation("Shipped icon manifest: {Count} icons under wwwroot/gfx", shippedIcons.Count);
    else
        app.Logger.LogWarning("Shipped icon manifest unavailable (WebRootPath {Path}); food icons stay local-first", app.Environment.WebRootPath);
}

// Configure the HTTP request pipeline.

// Use forwarded headers early to ensure HTTPS scheme detection works
app.UseForwardedHeaders();

// Request log = Serilog's default levels: exception or 5xx -> Error, everything else Information, which the
// RequestLoggingMiddleware=Warning override filters out. 4xx (empty /map/tiles cells, missing static assets,
// scanner probes) are client outcomes, counted per route template by the http.server.request.duration metric.
app.UseSerilogRequestLogging();

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

// Configure static file caching for better performance
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".js") || path.EndsWith(".css"))
        {
            ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Query.ContainsKey("v")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        }
        // Cache images for 1 day
        else if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".gif") || path.EndsWith(".ico"))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400"; // 1 day
        }
    }
});

// Use authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Enable output caching for tile endpoints
app.UseOutputCache();

// SSE proxy endpoint - forwards browser EventSource requests to API service
// This is needed because browsers can't use Aspire service discovery. In production Caddy routes
// /map/updates straight to the API (@mapsse), so this is the dev path (Aspire) and the prod fallback.
// Every connection is routine: lifecycle lines are Debug, auth refusals are client outcomes.
app.MapGet("/map/updates", async (HttpContext context, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
    {
        logger.LogDebug("[SSE Proxy] User not authenticated");
        return Results.Unauthorized();
    }

    var hasMapAuth = context.User.Claims.Any(c =>
        c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
        c.Value.Equals(Permission.Map.ToClaimValue(), StringComparison.OrdinalIgnoreCase));
    if (!hasMapAuth)
    {
        logger.LogDebug("[SSE Proxy] User lacks Map permission");
        return Results.Unauthorized();
    }

    try
    {
        // Create HTTP client to API service
        var apiClient = httpClientFactory.CreateClient("API");
        apiClient.Timeout = Timeout.InfiniteTimeSpan;

        // Forward request to API service.
        //
        // IMPORTANT:
        // The browser SSE client connects with `?since=<token>` to avoid re-downloading and re-parsing
        // the entire initial tile cache on reconnect.
        //
        // If we drop the query string here, the API always thinks `since=0` and will resend the full
        // tile cache snapshot (potentially ~300k tiles), which can freeze the browser for tens of seconds.
        //
        // Therefore we MUST forward the query string verbatim.
        var requestUri = "/map/updates" + (context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("Accept", "text/event-stream");

        // ResponseHeadersRead is load-bearing: the standard resilience handler's total timeout
        // completes at headers, not at end-of-stream, so the long-lived SSE body survives
        var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            // The API logs its own failures; for the browser this is just a failed connect it retries
            logger.LogDebug("[SSE Proxy] API returned {StatusCode}", response.StatusCode);
            return Results.StatusCode((int)response.StatusCode);
        }

        // Set SSE headers before writing anything
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        logger.LogDebug("[SSE Proxy] Streaming {RequestUri} from the API", requestUri);

        // Stream response from API to browser with buffering disabled
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);

        var buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        logger.LogDebug("[SSE Proxy] Stream ended");
        return Results.Empty;
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away, closed the tab, or map-updates.js replaced the stream — normal
        return Results.Empty;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SSE Proxy] Exception while proxying SSE");
        // Once streaming began the status line is gone; the browser just sees the stream end and reconnects
        return context.Response.HasStarted ? Results.Empty : Results.StatusCode(500);
    }
}).RequireAuthorization();

// Notification SSE proxy - forwards the notification bell's EventSource to the API service.
// Same mechanics as the /map/updates proxy above, but auth-only: notifications are for every
// authenticated user, so no Map-permission check. In production Caddy routes this path straight
// to the API (@notifsse); this proxy is the dev path (Aspire) and the prod fallback.
app.MapGet("/api/notifications/stream", async (HttpContext context, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    if (!context.User.Identity?.IsAuthenticated ?? true)
    {
        return Results.Unauthorized();
    }

    try
    {
        var apiClient = httpClientFactory.CreateClient("API");
        apiClient.Timeout = Timeout.InfiniteTimeSpan;

        var requestUri = "/api/notifications/stream" + (context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("Accept", "text/event-stream");

        // ResponseHeadersRead is load-bearing: the standard resilience handler's total timeout
        // completes at headers, not at end-of-stream, so the long-lived SSE body survives
        var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("[Notification SSE Proxy] API returned {StatusCode}", response.StatusCode);
            return Results.StatusCode((int)response.StatusCode);
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);

        var buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer, 0, bytesRead, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        return Results.Empty;
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away or closed the tab — normal
        return Results.Empty;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Notification SSE Proxy] Exception while proxying SSE");
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

    // Limit bounds to prevent excessive queries (150 allows viewing large zoomed-out areas)
    var maxRange = 150;
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

// WebP tile serving endpoint - 400x400 tiles generated on-the-fly by LargeTileService
app.MapGet("/map/tiles/{**path}", async (
    HttpContext context,
    string path,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db,
    HnHMapperServer.Services.Interfaces.ILargeTileService largeTileService) =>
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

    // Parse path: {mapId}/{zoom}/{x}_{y}.webp
    var parts = path.Split('/');
    if (parts.Length != 3)
        return Results.NotFound();

    if (!int.TryParse(parts[0], out var mapId))
        return Results.NotFound();

    if (!int.TryParse(parts[1], out var zoom))
        return Results.NotFound();

    var coordPart = parts[2].Replace(".webp", "");
    var coords = coordPart.Split('_');
    if (coords.Length != 2)
        return Results.NotFound();

    if (!int.TryParse(coords[0], out var x))
        return Results.NotFound();

    if (!int.TryParse(coords[1], out var y))
        return Results.NotFound();

    // Defense-in-depth: Verify map belongs to user's tenant before tile generation
    // The EF Core global filter also enforces this, but explicit check fails faster
    var mapExists = await db.Maps.AnyAsync(m => m.Id == mapId);
    if (!mapExists)
        return Results.NotFound();

    // Get or generate the large tile (returns bytes from in-memory cache, disk, or generation)
    byte[]? tileBytes;
    try
    {
        tileBytes = await largeTileService.GetOrGenerateLargeTileAsync(tenantId, mapId, zoom, x, y);
    }
    catch (Exception)
    {
        // Generation failed (the service logged it). Unlike an empty area this is temporary,
        // so it must not be cached like the 404 below: no-store lets the next view retry.
        context.Response.Headers.Append("Cache-Control", "no-store");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (tileBytes == null)
    {
        context.Response.Headers.Append("Cache-Control", "public, max-age=300, stale-while-revalidate=60");
        return Results.NotFound();
    }

    // Long-lived cache for tile hits (1 year)
    context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
    return Results.Bytes(tileBytes, "image/webp");
}).RequireAuthorization()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromSeconds(60))
      .SetVaryByQuery("v")
      .SetVaryByRouteValue("path")
      .Tag("tiles-webp"));

// Public map tile endpoint - serves tiles from in-memory cache (fastest)
// Falls back to filesystem for tiles loaded after startup
app.MapGet("/public/{slug}/tiles/{**path}", (
    HttpContext context,
    string slug,
    string path,
    PublicTileCacheService tileCache,
    IConfiguration configuration) =>
{
    // Validate and sanitize path to prevent directory traversal
    if (string.IsNullOrEmpty(path) || path.Contains(".."))
        return Results.BadRequest();

    // Try in-memory cache first (instant, no disk I/O)
    if (tileCache.TryGetTile(slug, path, out var cachedData) && cachedData != null)
    {
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(cachedData, "image/png");
    }

    // Fallback to filesystem for tiles added after startup
    var gridStorage = configuration["GridStorage"] ?? "map";
    var filePath = Path.Combine(gridStorage, "public", slug, path);

    if (!File.Exists(filePath))
    {
        // Cache 404s for 5 minutes to reduce repeated requests for missing tiles
        context.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=60";
        return Results.NotFound();
    }

    // Load from disk and add to cache for future requests
    var bytes = File.ReadAllBytes(filePath);
    tileCache.AddTile(slug, path, bytes);

    // Set aggressive caching headers - tiles are immutable once generated
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(bytes, "image/png");
}).AllowAnonymous()
  .CacheOutput(policy => policy
      .Expire(TimeSpan.FromMinutes(10))
      .SetVaryByRouteValue("slug", "path")
      .SetVaryByQuery("v")
      .Tag("public-tiles"));

// Internal endpoint for API to invalidate public map cache
// Called after delete/regenerate operations to clear in-memory and output cache
app.MapPost("/internal/public-cache/invalidate/{slug}", async (
    string slug,
    PublicTileCacheService tileCache,
    IOutputCacheStore outputCacheStore,
    ILogger<Program> logger) =>
{
    // Invalidate in-memory tile cache
    tileCache.InvalidateSlug(slug);

    // Evict ASP.NET OutputCache by tag
    await outputCacheStore.EvictByTagAsync("public-tiles", default);

    logger.LogInformation("Invalidated cache for public map: {Slug}", slug);
    return Results.Ok(new { invalidated = slug });
});

// Internal endpoint for API to invalidate specific tenant tile cache entries
// Called after ZoomTileProcessorService generates new tiles so Web serves fresh data
app.MapPost("/internal/tile-cache/invalidate", async (
    HttpContext context,
    HnHMapperServer.Services.Interfaces.ILargeTileService largeTileService,
    IOutputCacheStore outputCacheStore,
    ILogger<Program> logger) =>
{
    var tiles = await context.Request.ReadFromJsonAsync<TileCacheInvalidationRequest[]>();
    if (tiles == null || tiles.Length == 0)
        return Results.BadRequest();

    foreach (var tile in tiles)
    {
        largeTileService.InvalidateCachedTile(tile.TenantId, tile.MapId, tile.BaseX, tile.BaseY);
    }

    await outputCacheStore.EvictByTagAsync("tiles-webp", default);
    logger.LogDebug("Invalidated {Count} tile cache entries", tiles.Length);
    return Results.Ok();
}).DisableAntiforgery();

// Internal endpoint for API to drop this process's in-memory WebP cache for a whole map.
// Called after bulk pyramid operations (superadmin rebuild, region wipe) that delete files on disk.
app.MapPost("/internal/tile-cache/invalidate-map", async (
    HttpContext context,
    HnHMapperServer.Services.Interfaces.ILargeTileService largeTileService,
    IOutputCacheStore outputCacheStore,
    ILogger<Program> logger) =>
{
    var request = await context.Request.ReadFromJsonAsync<MapTileCacheInvalidationRequest>();
    if (request == null || string.IsNullOrEmpty(request.TenantId))
        return Results.BadRequest();

    largeTileService.InvalidateMapCache(request.TenantId, request.MapId);
    await outputCacheStore.EvictByTagAsync("tiles-webp", default);
    logger.LogInformation("Invalidated map tile cache for tenant {TenantId} map {MapId}", request.TenantId, request.MapId);
    return Results.Ok();
}).DisableAntiforgery();

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
      .Expire(TimeSpan.FromMinutes(5))
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
      .Expire(TimeSpan.FromMinutes(5)));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Convenience redirect for any stray Identity UI redirects
app.MapGet("/Account/Login", () => Results.Redirect("/login")).DisableAntiforgery();

// Local login endpoint (validates via Identity, signs shared cookie for Web domain)
app.MapPost("/api/login", async (
    HttpContext context,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db) =>
{
    string username = string.Empty;
    string password = string.Empty;
    string? returnUrl = null;
    try
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            username = form["user"].ToString();
            if (string.IsNullOrEmpty(username)) username = form["username"].ToString();
            password = form["pass"].ToString();
            if (string.IsNullOrEmpty(password)) password = form["password"].ToString();
            returnUrl = form["returnUrl"].ToString();
        }
        else
        {
            var body = await context.Request.ReadFromJsonAsync<LoginPayload>();
            username = body?.Username ?? string.Empty;
            password = body?.Password ?? string.Empty;
            returnUrl = body?.ReturnUrl;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/login?error=1");

        var user = await userManager.FindByNameAsync(username);
        if (user == null) return Results.Redirect("/login?error=1");
        var valid = await userManager.CheckPasswordAsync(user, password);
        if (!valid) return Results.Redirect("/login?error=1");

        // Approved memberships in active tenants decide where the user lands. Zero memberships is a normal
        // state now: the user still gets a session and is routed to the create-or-join screen.
        var memberTenantIds = await db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => tu.UserId == user.Id && tu.JoinedAt != default)
            .Join(db.Tenants.IgnoreQueryFilters().Where(t => t.IsActive),
                tu => tu.TenantId, t => t.Id, (tu, t) => tu.TenantId)
            .ToListAsync();

        if (memberTenantIds.Count == 1)
            user.ActiveTenantId = memberTenantIds[0];   // a single membership is always the active one
        else if (memberTenantIds.Count == 0)
            user.ActiveTenantId = null;
        user.LastLoginAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        // SignInManager uses the shared TenantClaimsPrincipalFactory to add the active tenant's claims
        await signInManager.SignInAsync(user, isPersistent: true);

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("User {Username} logged in successfully ({TenantCount} tenant(s))", username, memberTenantIds.Count);

        var activeIsValid = user.ActiveTenantId != null && memberTenantIds.Contains(user.ActiveTenantId);
        if (LocalUrl.IsLocal(returnUrl))
            return Results.LocalRedirect(returnUrl!);   // explicit destination (e.g. an invite landing page) wins
        if (memberTenantIds.Count == 0 || !activeIsValid)
            return Results.LocalRedirect("/tenant/select");
        return Results.LocalRedirect("/");
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Login failed for user {Username}", username);
        return Results.Redirect("/login?error=1");
    }
}).DisableAntiforgery();

// Tenant switching - the ONLY switching path used by the UI. Persists the choice on the user row and re-issues the
// Web cookie with the new tenant's claims (only this process can set the browser cookie). Other tabs/devices
// converge through normal cookie revalidation; no security-stamp bump (that would sign the user out everywhere).
// Blazor callers must navigate here with forceLoad - a circuit cannot set cookies.
app.MapGet("/api/tenant/select", async (
    HttpContext context,
    string? tenantId,
    string? returnUrl,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db) =>
    await TenantSwitch.HandleAsync(context, tenantId, returnUrl, userManager, signInManager, db))
    .DisableAntiforgery();

app.MapPost("/api/tenant/select", async (
    HttpContext context,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    HnHMapperServer.Infrastructure.Data.ApplicationDbContext db) =>
{
    var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync() : null;
    return await TenantSwitch.HandleAsync(context, form?["tenantId"], form?["returnUrl"], userManager, signInManager, db);
}).DisableAntiforgery();

// External sign-in routes (each answers 404 while its provider is switched off)
HnHMapperServer.Web.Security.ExternalAuthEndpoints.MapExternalAuthEndpoints(app);

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
    public string? ReturnUrl { get; set; }
}

/// <summary>Open-redirect guard: only same-site absolute paths ("/foo") are accepted as redirect targets.</summary>
file static class LocalUrl
{
    public static bool IsLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal);
}

file static class TenantSwitch
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        string? tenantId,
        string? returnUrl,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        HnHMapperServer.Infrastructure.Data.ApplicationDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Results.Redirect("/login");

        var user = await userManager.GetUserAsync(context.User);
        if (user == null)
            return Results.Redirect("/login");

        if (string.IsNullOrWhiteSpace(tenantId))
            return Results.Redirect("/tenant/select?error=missing");

        var isApprovedMember = await db.TenantUsers
            .IgnoreQueryFilters()
            .AnyAsync(tu => tu.UserId == user.Id && tu.TenantId == tenantId && tu.JoinedAt != default);
        var tenantActive = isApprovedMember && await db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == tenantId && t.IsActive);

        if (!tenantActive)
            return Results.Redirect("/tenant/select?error=not_member");

        if (!string.Equals(user.ActiveTenantId, tenantId, StringComparison.Ordinal))
        {
            user.ActiveTenantId = tenantId;
            await userManager.UpdateAsync(user);
        }

        // Re-issue the cookie: the claims factory now emits the selected tenant's claims
        await signInManager.SignInAsync(user, isPersistent: true);

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("User {UserId} switched active tenant to {TenantId}", user.Id, tenantId);

        return Results.LocalRedirect(LocalUrl.IsLocal(returnUrl) ? returnUrl! : "/");
    }
}

// DTO for cross-process tile cache invalidation
file sealed record TileCacheInvalidationRequest(string TenantId, int MapId, int BaseX, int BaseY);

// DTO for cross-process whole-map tile cache invalidation (superadmin rebuild / region wipe)
file sealed record MapTileCacheInvalidationRequest(string TenantId, int MapId);

 
