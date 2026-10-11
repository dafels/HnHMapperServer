# HnH Mapper Server - Project Documentation for AI Assistants

**Last Updated:** 2026-08-23
**Project Status:** Production-Ready (Core + Admin + Multi-Tenancy + Cookbook)
**Tech Stack:** .NET 10 (LTS), ASP.NET Core, Blazor Server (Workstation GC), MudBlazor 9, SQLite, .NET Aspire 13, Docker
**Current Branch:** `master` (the .NET 10 / MudBlazor 9 upgrade lives on `upgrade/net10`)

---

## Project Overview

Complete .NET 9 implementation of the Haven & Hearth (HnH) Auto-Mapper Server with multi-tenancy support:
- **Game Client APIs** - Map tile uploads, character tracking, marker management
- **Web UI** - User dashboard, map viewing, multi-tenant admin panel
- **Multi-Tenancy** - Invitation-based registration, tenant isolation, storage quotas
- **Real-time Updates** - Server-Sent Events (SSE) for characters and markers

**Key Achievement:** 100% backward compatibility with existing HnH game clients while adding enterprise multi-tenancy features.

---

## Architecture

### Technology Stack

| Layer | Technology |
|-------|-----------|
| **Framework** | .NET 9.0 |
| **Web** | ASP.NET Core (Minimal APIs), Blazor Server |
| **UI** | MudBlazor 8.13.0 |
| **Orchestration** | .NET Aspire |
| **Database** | SQLite with Entity Framework Core |
| **Auth** | ASP.NET Core Identity + Data Protection API |
| **Image Processing** | SixLabors.ImageSharp |
| **Real-time** | System.Threading.Channels (SSE) |
| **Logging** | Serilog |

### Project Structure

```
HnHMapperServer/
├── src/
│   ├── HnHMapperServer.AppHost/         # .NET Aspire orchestration
│   ├── HnHMapperServer.ServiceDefaults/ # Aspire defaults (telemetry, health)
│   ├── HnHMapperServer.Core/            # Domain layer
│   │   ├── Models/                      # Domain entities (Character, Marker, Tenant, etc.)
│   │   ├── DTOs/                        # Data transfer objects
│   │   ├── Enums/                       # TenantRole, Permission
│   │   ├── Extensions/                  # Extension methods
│   │   └── Constants/                   # Constants
│   ├── HnHMapperServer.Infrastructure/  # Data access layer
│   │   ├── Data/ApplicationDbContext.cs # EF Core DbContext with tenant filters
│   │   ├── Entities/                    # EF Core entities
│   │   └── Repositories/                # Repository implementations
│   ├── HnHMapperServer.Services/        # Business logic layer
│   │   ├── Services/                    # Service implementations
│   │   │   ├── TenantNameService.cs     # Tenant ID generation
│   │   │   ├── TenantContextAccessor.cs # Tenant context resolution
│   │   │   ├── StorageQuotaService.cs   # Storage quota management
│   │   │   ├── AuditService.cs          # Audit logging
│   │   │   └── ...
│   │   └── Interfaces/                  # Service contracts
│   ├── HnHMapperServer.Api/             # Game client API service
│   │   ├── BackgroundServices/          # CharacterCleanup, MarkerReadiness, etc.
│   │   ├── Endpoints/                   # Minimal API endpoints
│   │   │   ├── ClientEndpoints.cs       # Game client APIs (9 endpoints)
│   │   │   ├── MapEndpoints.cs          # Map viewing APIs (SSE, tiles)
│   │   │   ├── CustomMarkerEndpoints.cs # Custom markers (5 endpoints)
│   │   │   ├── TenantAdminEndpoints.cs  # Tenant admin APIs (10 endpoints)
│   │   │   ├── SuperadminEndpoints.cs   # Superadmin APIs (13 endpoints)
│   │   │   ├── InvitationEndpoints.cs   # Invitation management
│   │   │   ├── AuditEndpoints.cs        # Audit logs
│   │   │   └── DatabaseEndpoints.cs     # Database viewer
│   │   ├── Authorization/               # Custom authorization handlers
│   │   │   ├── SuperadminOnlyHandler.cs
│   │   │   ├── TenantAdminHandler.cs
│   │   │   └── TenantPermissionHandler.cs
│   │   ├── Middleware/
│   │   │   └── TenantContextMiddleware.cs # Tenant resolution from token/claims
│   │   ├── Security/
│   │   │   └── TenantClaimsPrincipalFactory.cs # Tenant claims injection
│   │   └── Program.cs
│   └── HnHMapperServer.Web/             # Blazor Web UI service
│       ├── Components/
│       │   ├── Pages/                   # Blazor pages
│       │   │   ├── Login.razor          # Multi-tenant login
│       │   │   ├── Register.razor       # Invitation-based registration
│       │   │   ├── Index.razor          # Dashboard
│       │   │   ├── Map.razor            # Map viewer
│       │   │   ├── Admin.razor          # Admin panel (tenant-scoped)
│       │   │   ├── SuperAdmin.razor     # Superadmin panel
│       │   │   ├── TenantDetails.razor  # Tenant details
│       │   │   ├── PendingApproval.razor # User approval workflow
│       │   │   └── PendingAssignment.razor # Superadmin assignment
│       │   ├── Admin/                   # Admin panel components
│       │   │   ├── UserManagement.razor
│       │   │   ├── TokenManagement.razor
│       │   │   ├── InvitationManagement.razor
│       │   │   ├── PendingUsers.razor
│       │   │   ├── TenantAuditLogs.razor
│       │   │   ├── TenantSettings.razor
│       │   │   ├── MapManagement.razor
│       │   │   └── ...
│       │   └── SuperAdmin/              # Superadmin components
│       │       ├── TenantList.razor
│       │       ├── UnassignedUsersList.razor
│       │       ├── GlobalAuditLogs.razor
│       │       └── ...
│       ├── Security/
│       │   └── TenantClaimsPrincipalFactory.cs
│       └── Program.cs
├── tools/                               # Development tools (gitignored)
├── deploy/                              # Docker deployment configs
│   ├── docker-compose.yml
│   ├── Caddyfile
│   ├── VPS-SETUP.md
│   └── SECURITY.md
└── map/                                 # Data storage (runtime)
    ├── grids.db                         # SQLite database
    ├── tenants/{tenantId}/grids/        # Tenant-isolated tile storage
    └── DataProtection-Keys/             # Shared cookie encryption keys
```

---

## Current Implementation Status

### ✅ Multi-Tenancy (FULLY IMPLEMENTED)

The application is a **fully multi-tenant system** on the `tenancy` branch:

**Core Features:**
- **Tenant Isolation**: Complete data isolation via EF Core global query filters
- **Self-service onboarding**: players create their own tenant ("map") or join one through a multi-use
  invite link that joins immediately (no approval queue); optional Steam / Discord sign-in
- **Role Hierarchy**: SuperAdmin, TenantAdmin, TenantUser with granular permissions
- **Storage Quotas**: Per-tenant storage limits with real-time tracking
- **Audit Logging**: Comprehensive audit trail for all sensitive operations
- **Token Format**: Tenant-prefixed tokens (`{tenantId}_{secret}`) with backward compatibility

**Authentication:**
- ASP.NET Core Identity (AspNetUsers, AspNetRoles tables)
- Multi-tenant login flow with tenant selection
- Users can belong to multiple tenants
- Tenant context resolved from token or claims

**Key Endpoints:**
- **TenantAdmin** (10 endpoints): User management, invitations, audit logs
- **Superadmin** (13 endpoints): Tenant management, unassigned users, global audit
- **Invitation** (5 endpoints): Validate (public preview), redeem (signed-in), create / list / revoke (TenantAdmin of that tenant)
- **Self-service** (2 endpoints): `POST /api/tenants/self` (create a map, become its admin), `GET /api/tenants/self/options`
- **Accounts overview**: `GET /api/superadmin/accounts` (paged, filterable "who joined how")

**UI Components:**
- Tenant admin panel with tabs: Users, Tokens, Invitations, Pending Users, Audit Logs, Maps, Config
- Superadmin panel: Tenant list, unassigned users, global audit logs
- Pending approval workflow for new users
- Tenant selector dropdown in navbar

**Background Services:**
- `InvitationExpirationService`: Auto-expires invitations after 7 days
- `TenantStorageVerificationService`: Verifies storage quotas

**Database:**
- 5 new tables: Tenants, TenantUsers, TenantPermissions, TenantInvitations, AuditLogs
- All existing tables have TenantId column
- 7+ migrations applied (AddMultiTenancy, SeedDefaultTenant, UpdateExistingTokensFormat, etc.)

### ✅ Game Client APIs (9/9 endpoints)

All endpoints tenant-scoped and backward compatible:

| Endpoint | Purpose |
|----------|---------|
| `POST /client/{token}/checkVersion` | Version 4 validation |
| `GET /client/{token}/locate` | Grid location lookup |
| `POST /client/{token}/gridUpdate` | Map synchronization with merge logic |
| `POST /client/{token}/gridUpload` | Tile upload with winter season logic |
| `POST /client/{token}/positionUpdate` | Character tracking |
| `POST /client/{token}/markerBulkUpload` | Bulk marker creation |
| `POST /client/{token}/markerDelete` | Marker deletion |
| `POST /client/{token}/markerUpdate` | Marker status updates |
| `POST /client/{token}/markerReadyTime` | Harvest timer updates |

### ✅ Map Viewing & Real-time Updates

**SSE Endpoints:**
- `GET /map/updates` - Server-Sent Events for real-time character and marker updates
- 500ms server-side drain loop over per-event channels, filtered by tenant
- Events: tile batches (default), `charactersSnapshot` (initial), `characterDelta`, `merge`,
  `mapUpdate` (**upsert** — full map item incl. new maps), `mapDelete`, `mapRevision` (+ `cells` on ZoomQ bumps),
  `customMarker*`, `marker*`, `ping*`, `road*`, `timer*`, `notification*`, `overlayUpdated`
- Liveness: a named `heartbeat` event (`Core/Constants/SseFrames`) when the loop starts and every 5 s;
  `map-updates.js` reconnects (manual backoff, `?since=` rebuilt) after 20 s without any event. The server ends
  each stream after ~15 min (re-authorization + resync; the client is back within ~1 s). No polling
  fallback (removed 2026-10-11) — see that Recent Changes entry

**Map APIs:**
- `GET /map/api/v1/characters` - Character list (deprecated, use SSE)
- `GET /map/api/v1/markers` - Marker list
- `GET /map/tiles/{mapid}/{zoom}/{x}_{y}.webp?v={revision}` - 400px WebP tiles, zoom 0-6 (served by the Web)
- `GET /map/api/maps` - Map list
- `GET /map/api/config` - Runtime configuration

**Admin Map Operations:**
- `POST /map/api/admin/wipeTile` - Delete tile
- `POST /map/api/admin/setCoords` - Update coordinates
- `POST /map/api/admin/hideMarker` - Hide marker
- `POST /map/api/admin/deleteMarker` - Delete marker

### ✅ Custom Markers (5/5 endpoints)

User-placed annotations with authorization:

| Endpoint | Authorization |
|----------|---------------|
| `GET /map/api/v1/custom-markers` | Permission: Map |
| `GET /map/api/v1/custom-markers/{id}` | Permission: Map |
| `POST /map/api/v1/custom-markers` | Permission: Markers |
| `PUT /map/api/v1/custom-markers/{id}` | Creator or TenantAdmin |
| `DELETE /map/api/v1/custom-markers/{id}` | Creator or TenantAdmin |

**Features:**
- Icon whitelist validation via `IIconCatalogService`
- HTML sanitization (strips all tags)
- Coordinate clamping (0-100 range)
- Real-time SSE updates

### ✅ Background Services

| Service | Interval | Purpose |
|---------|----------|---------|
| `CharacterCleanupService` | 10s | Remove stale characters (timeout: 10s) |
| `MarkerReadinessService` | 30s | Update marker ready status |
| `MapCleanupService` | 10min | Delete empty maps older than 1 hour |
| `InvitationExpirationService` | 1 hour | Expire old invitations |
| `TenantStorageVerificationService` | 6 hours (first run after `StorageVerification:StartupDelayMinutes`, 60) | Reset tracked storage to disk; warn on sign-aware drift |
| `NotificationCleanupService` | 30min | Delete expired notifications (all tenants), broadcast dismissals |

---

## Authentication & Authorization

### ASP.NET Core Identity

**Migration from custom auth completed:**
- Uses ASP.NET Identity with AspNetUsers, AspNetRoles tables
- Password hashing via Identity (PBKDF2)
- Cookie-based authentication with Data Protection API
- Shared keys in `map/DataProtection-Keys/` for Web/API cookie sharing

**Multi-Tenant Authentication Flow:**
1. User logs in at `/login` (password → Web `/api/login`; or Steam / Discord → `/auth/{provider}/callback`)
2. The Web process mints the cookie; the ONE shared `Infrastructure/Identity/TenantClaimsPrincipalFactory`
   (registered by both Web and API) injects the claims of the user's **active tenant** =
   `AspNetUsers.ActiveTenantId` when it is still an approved membership in an active tenant, else the
   oldest approved membership (`ActiveTenantMembershipResolver`); zero memberships → no tenant claims
   (a normal state: the user is routed to `/tenant/select` to create or join a map)
3. Switching = Web `GET|POST /api/tenant/select?tenantId=&returnUrl=` (persists `ActiveTenantId`, re-issues
   the cookie; Blazor callers navigate with `forceLoad`). Other tabs/devices converge without a security-stamp
   bump: the Web cookie validator rebuilds when the cookie's TenantId ≠ resolved active tenant, the API's
   Identity validator rebuilds from the DB every 10 s, and the Blazor revalidation provider bounces stale circuits
4. `TenantContextMiddleware` resolves tenant from token or claims
5. All database queries automatically filtered by tenant via EF Core global query filters

### Authorization Hierarchy

**Roles (TenantRole enum):**
- **SuperAdmin**: Full system access, manage all tenants
- **TenantAdmin**: Manage users within their tenant, create invitations
- **TenantUser**: Standard user with configurable permissions

**Permissions (Permission enum):**
- **Map**: View maps
- **Markers**: View and create markers
- **Pointer**: View character positions
- **Upload**: Upload tiles via game client
- **Writer**: Edit/delete tiles and markers

**Authorization Handlers:**
- `SuperadminOnlyHandler`: Enforces SuperAdmin role
- `TenantAdminHandler`: Enforces TenantAdmin or higher
- `TenantPermissionHandler`: Enforces granular permissions

### Token Format

**Multi-tenant tokens:** `{tenantId}_{secret}`
- Example: `warrior-shield-42_a1b2c3d4e5f6...`
- Tenant ID extracted from token prefix
- Backward compatible with old tokens via migration layer

---

## Database Schema

### Core Tables (Tenant-Scoped)

All tables have `TenantId TEXT NOT NULL` column with indexes.

**Maps:**
```sql
CREATE TABLE Maps (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TenantId TEXT NOT NULL,
    Name TEXT NOT NULL,
    Hidden INTEGER NOT NULL,
    Priority INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id)
);
```

**Grids, Tiles, Markers, CustomMarkers**: Similar structure with TenantId foreign key.

**Tokens:**
```sql
CREATE TABLE Tokens (
    Token TEXT PRIMARY KEY,
    TenantId TEXT NOT NULL,
    UserId TEXT NOT NULL,
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id),
    FOREIGN KEY (UserId) REFERENCES AspNetUsers(Id)
);
```

**Config:**
```sql
CREATE TABLE Config (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL,
    TenantId TEXT NOT NULL
);
```

### Multi-Tenancy Tables

**Tenants:**
```sql
CREATE TABLE Tenants (
    Id TEXT PRIMARY KEY,              -- e.g., "warrior-shield-42"
    Name TEXT NOT NULL,
    StorageQuotaMB INTEGER NOT NULL DEFAULT 1024,
    CurrentStorageMB REAL NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1
);
```

**TenantUsers (many-to-many):**
```sql
CREATE TABLE TenantUsers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TenantId TEXT NOT NULL,
    UserId TEXT NOT NULL,             -- AspNetUsers.Id
    Role TEXT NOT NULL,               -- TenantAdmin or TenantUser
    JoinedAt TEXT NOT NULL,
    PendingApproval INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id),
    FOREIGN KEY (UserId) REFERENCES AspNetUsers(Id)
);
```

**TenantPermissions:**
```sql
CREATE TABLE TenantPermissions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TenantUserId INTEGER NOT NULL,
    Permission TEXT NOT NULL,         -- Map, Markers, Pointer, Upload, Writer
    FOREIGN KEY (TenantUserId) REFERENCES TenantUsers(Id)
);
```

**TenantInvitations:**
```sql
CREATE TABLE TenantInvitations (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TenantId TEXT NOT NULL,
    InviteCode TEXT NOT NULL UNIQUE,
    CreatedBy TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,          -- 7 days from creation
    UsedBy TEXT,
    UsedAt TEXT,
    Status TEXT NOT NULL DEFAULT 'Active',
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id)
);
```

**AuditLogs:**
```sql
CREATE TABLE AuditLogs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    UserId TEXT,
    TenantId TEXT,
    Action TEXT NOT NULL,
    EntityType TEXT,
    EntityId TEXT,
    OldValue TEXT,
    NewValue TEXT,
    IpAddress TEXT,
    UserAgent TEXT
);
```

**ASP.NET Identity Tables:**
- AspNetUsers, AspNetRoles, AspNetUserRoles, AspNetUserClaims, etc. (standard Identity schema)

---

## Configuration

### appsettings.json

```json
{
  "GridStorage": "map",
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Cleanup": {
    "DeleteEmptyMapsAfterMinutes": 60,
    "MapCleanupIntervalSeconds": 600
  }
}
```

### Production Configuration (appsettings.Production.json)

**Security defaults:**
- `EnableCors`: false (CORS disabled by default)
- `EnableHttpsRedirect`: false (allows IP-only HTTP)
- `SelfRegistration.Enabled`: false (invitation-only registration)
- `BootstrapAdmin.Enabled`: true (creates default admin user)

### Environment Variables

- `GridStorage`: Data directory (default: "map"). Both Program.cs files resolve it once (a relative value is
  taken relative to the solution folder, i.e. `src/map` in dev) and publish the absolute path back into
  configuration, so every `configuration["GridStorage"]` reader agrees with the database path regardless of the
  process working directory.
- `Cleanup:DeleteEmptyMapsAfterMinutes`: Empty map retention (default: 60)
- `Cleanup:MapCleanupIntervalSeconds`: Cleanup interval (default: 600)

### Runtime Configuration

Stored in `Config` table (tenant-scoped):
- `title`: Site title
- `prefix`: URL prefix for token display
- `defaultHide`: Default hidden status for new maps

### Logging levels and telemetry rules

- **Policy.** Error = a user-visible failure or a server bug (unhandled exception, 5xx, data loss).
  Warning = degraded/unexpected and operator-actionable, **never a routine client outcome**.
  Information = one record per lifecycle/business event, never per request or per tick.
  Debug = per-request/item/tick diagnostics and anything carrying player names.
- **`Serilog:MinimumLevel` is the only level gate** for Console, File and OTLP: `UseSerilog(writeToProviders)`
  + `ClearProviders` make `Logging:LogLevel` inert. An environment file can overwrite keys but never remove
  them, so **never put Debug overrides in a base `appsettings.json`** — use `appsettings.Development.json`, or
  during an incident `Serilog__MinimumLevel__Override__<Category>=Debug` on the container (SignalR /
  Http.Connections Debug logs circuit secrets and form input: env var only, never committed).
  `LoggingConfigurationTests` enforces this.
- **Request log** = Serilog's default levels (exception/5xx = Error, else Information, filtered by the
  `RequestLoggingMiddleware=Warning` override): 4xx are not logged — count them in
  `http.server.request.duration` (route template, no token). 429s are logged once by the API's `OnRejected`
  (policy + route template, never path or IP).
- **Secrets in URLs.** Never log `Request.Path`/URIs of secret-bearing routes, player names or IPs, and never
  interpolate values into a log template. `ServiceDefaults/Telemetry/SensitiveUrlRedactor` is THE list of
  secret-bearing URLs (`/client/{token}`, invite codes, token revoke, outbound Discord webhooks); the Serilog
  root enricher (added by ServiceDefaults' `ApplyServiceDefaults()`, which must stay the LAST call in both
  `UseSerilog` lambdas — `LoggingConfigurationTests` checks it) and the span processor apply it to every log
  property and `url.*` tag. It cannot see template text, exception messages or structure/dictionary values.
  **Adding a route or HttpClient call with a secret in the path = extend the redactor and its tests.**
- **Stale /client tokens:** `TenantContextMiddleware` logs each rejection at Debug and tags
  `http.server.request.duration` with `hnhmapper.client_token.rejection`; `ClientTokenRejectionTracker` emits one
  Warning per (tenant, token) at first sighting and a summary per 15 min — tenant-named only for an existing
  tenant and a canonical secret, never the token, its hash or the IP.
- Components log through `ILogger`, never `Console.WriteLine` (bypasses levels and the dashboard); failures
  caught in components use `UiLog.FailureLevel(ex)` (Warning, Debug for circuit-gone/cancelled). An expected
  outcome is not a failure: catch it first at Debug (e.g. EventsPanel's 404 for a deleted custom marker).
- The aspnet base image provides `ASPNETCORE_HTTP_PORTS=8080`; do not set `ASPNETCORE_URLS` (startup warning).

---

## Running the Application

### Development

```bash
# From HnHMapperServer/src directory
cd HnHMapperServer.AppHost
dotnet run
```

**Aspire Dashboard** opens automatically showing service logs, metrics, and health checks.

### Production Deployment

**Docker Compose stack** (4 services):
- `api`: Game client APIs + admin APIs (port 8080 internal)
- `web`: Blazor UI (port 8080 internal)
- `caddy`: Reverse proxy with path-based routing (port 80 external)
- `watchtower`: Auto-updates from GitHub Container Registry

**Deployment:**
```bash
cd deploy
docker compose up -d
```

**CI/CD:** Push to `main` branch → GitHub Actions builds images → Watchtower deploys within 60 seconds.

See `deploy/VPS-SETUP.md` for full deployment guide.

### Default Credentials

**First-time setup:**
- Username: `admin`
- Password: `admin123!`

⚠️ **Change immediately after first login!**

---

## Key Implementation Details

### Map Merging Logic

When `gridUpdate` receives grids spanning multiple maps:
1. Group grids by coordinate ranges
2. Calculate offsets (min X/Y for each detected map)
3. Choose target map or create new
4. Shift coordinates to target map's offset
5. Save grids with correct MapId and TenantId
6. Broadcast merge via SSE

### Real-time Updates (SSE)

**Character Streaming:**
- Replaced HTTP polling with Server-Sent Events
- Single persistent connection per client
- Initial snapshot: `event: charactersSnapshot`
- Updates: `event: characterDelta`
- Server-side coalescing (250ms batches)
- Backpressure handling (bounded channels, capacity 1024, DropOldest)

**Custom Marker Events:**
- `customMarkerCreated`, `customMarkerUpdated`, `customMarkerDeleted`

**Implementation:** `MapEndpoints.cs` lines 235-497

### Image Processing

Zoom levels 1-6 generated from base zoom-0 tiles:
1. Client uploads 100x100px PNG at zoom 0
2. For each zoom level: load 4 sub-tiles (2x2), combine with BiLinear interpolation, scale by factor of 2
3. Cache in `Tiles` table

### Storage Quotas

**Contract (2026-10-11):**
- `Tenants.CurrentStorageMB` changes only through single atomic delta UPDATEs
  (`IStorageQuotaService.IncrementStorageUsageAsync`, clamped at 0), each charging the **change in bytes on
  disk** (new size minus the replaced file's: `ITileService.ReplaceTileFileAsync`, `ZoomTileCache`, the merge's
  `DeleteRetiredMapDirectoriesAsync`). The increment does not save the caller's pending changes and does not
  refresh a tracked tenant; `TenantRepository.UpdateAsync` never writes `CurrentStorageMB`/`LastActivityAt`.
- `tenants/{t}/large/**` (WebP pyramid) is **reconciled-only**: writes never charged, deletes never credited.
- The scan is authoritative: `ReconcileStorageUsageAsync` resets the value to everything under
  `tenants/{t}/` — every 6 h (`TenantStorageVerificationService`) and after every admin teardown
  (`RecalculateStorageUsageAsync`: wipe, purge, map delete, integrity purge, failed import).
- The 6-hourly job warns only on drift that matters: overcount > max(2 MB, 0.5% of quota), undercount <
  −max(10 MB, 2% of quota) (`StorageDrift`; keys `StorageVerification:IntervalHours`, `StartupDelayMinutes`,
  `OvercountWarnMinMB`, `OvercountWarnPercentOfQuota`, `UndercountWarnMinMB`, `UndercountWarnPercentOfQuota`).
- gridUpload gates on **growth** (413 when current + growth > quota): refreshing an existing grid never 413s
  a tenant at its quota; a tenant over its quota is blocked. UI gauge shows usage percentage.

### Tenant Isolation

**EF Core Global Query Filters:**
```csharp
modelBuilder.Entity<Map>().HasQueryFilter(m => m.TenantId == _tenantContext.TenantId);
```
- All queries automatically filtered by tenant
- No manual TenantId checks required in business logic
- Prevents cross-tenant data leakage

### Blazor circuit and JS interop rules

- **Never inject scoped/circuit services into an IHttpClientFactory DelegatingHandler.** The factory resolves
  handlers in its own scope, shared by every user for the 2-minute handler lifetime, so "scoped" there means
  "shared across users". Read circuit state through `CircuitServicesAccessor` (see
  `AuthenticationDelegatingHandler`); keep handlers stateless.
- **Create API-calling timers and loops only in component lifecycle or event code** (an inbound circuit
  activity), never in a CircuitHandler or a `ReconnectionState` callback (`OnConnectionUp` is not an inbound
  activity: such a timer has no circuit identity and its calls go out unauthenticated). To resume work after a
  reconnect, re-arm the existing timer.
- **Never start JS interop while `ReconnectionState.IsConnected` is false.** The call is never delivered and is
  not replayed after a reconnect; it only ends as a TaskCanceledException after `JSInteropDefaultCallTimeout`
  (2 min). `SafeJsInterop` enforces this; direct `IJSObjectReference` calls must check it. Never report a JS
  failure through another JS call.
- **`DisposeAsync` must never throw** — an exception escaping a component's DisposeAsync terminates the circuit.
  Skip JS teardown while disconnected, keep a broad catch around every step, always dispose timers and
  DotNetObjectReferences.
- **No `requestAnimationFrame` (or other visibility-dependent) waits in a JS function .NET awaits** — browsers
  pause rAF in hidden tabs, so the call hangs until the interop timeout. Fire-and-forget rAF batching is fine.
- Pages that load data in `OnInitializedAsync` return early during static prerender (`HttpContext != null`,
  the cascading parameter) instead of fetching everything twice.
- Web-level tests (bUnit components, circuit handlers, Web services) live in `HnHMapperServer.Web.Tests`;
  `HnHMapperServer.Tests` must not reference the Web project.

---

## Security

### Production Security Measures

**Fixed Vulnerabilities:**
- CORS disabled by default (was allowing any origin with credentials)
- HTTPS redirect opt-in (was forced, broke IP-only deployments)
- Detailed errors disabled in production (prevents info disclosure)

**Security Features:**
- ASP.NET Identity password hashing (PBKDF2)
- SHA-256 token storage (tokens never stored plaintext)
- EF Core query filters (automatic tenant isolation)
- HTML sanitization for custom markers
- File path validation for tile access
- SQL injection protection (EF Core parameterized queries)

**Caddy Security Headers:**
```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
Content-Security-Policy: (tuned for Blazor Server)
-Server
```

**Forwarded Headers:**
- Respects `X-Forwarded-Proto` and `X-Forwarded-For` from reverse proxy

See `deploy/SECURITY.md` for complete security checklist.

---

## Testing

### Automated tests

- `src/HnHMapperServer.Tests` — Core, Services, Infrastructure (real SQLite where EF behaviour matters; never
  call `SqliteConnection.ClearAllPools`). It must not reference the Web project.
- `src/HnHMapperServer.Web.Tests` — Web-level: bUnit component tests, circuit handlers, the Web→API
  `AuthenticationDelegatingHandler`, Web services. Building it builds Web: not during a live VS/Aspire session.
- CI runs both through the solution (`dotnet test` at the repo root).

### Manual Testing Checklist

**Multi-Tenancy:**
- [ ] Invitation-based registration works
- [ ] Admin approval workflow functions
- [ ] Tenant switching works for multi-tenant users
- [ ] Data isolation verified (can't see other tenant's data)
- [ ] Storage quota enforcement works
- [ ] Superadmin can manage all tenants

**Game Client:**
- [ ] Upload tiles with tenant-prefixed token
- [ ] Character tracking updates in real-time
- [ ] Markers sync correctly

**SSE:**
- [ ] Single stable SSE connection per client
- [ ] Character deltas appear within 250ms
- [ ] No HTTP polling requests in Network tab
- [ ] EventStream shows `heartbeat` every 5 s; one `/map/updates` request stays open for 10+ min, and after
  12–18 min the server ends it and one new `/map/updates?since=…` follows within ~1 s (no chip)

**Admin Panel:**
- [ ] Create/edit/delete users within tenant
- [ ] Generate and revoke tokens
- [ ] View audit logs (tenant-scoped)
- [ ] Manage invitations

**Superadmin:**
- [ ] View all tenants
- [ ] Manage unassigned users
- [ ] View global audit logs
- [ ] Adjust storage quotas

### Known Limitations

1. **Rebuild Zoom Tiles**: Placeholder implementation (endpoint exists but doesn't rebuild)
2. **Export/Import**: Not implemented (manual database copy required)
3. **Map Management UI**: Limited (can't edit map properties from UI)

---

## Troubleshooting

### "401 Unauthorized when accessing admin endpoints"

**Cause:** Cookie not forwarded or tenant context missing
**Solution:** Verify `AuthenticationDelegatingHandler` registered and Data Protection keys shared. Its Warning
names the `AuthSource`: `Circuit` = the circuit's captured `HnH.Auth` cookie (captured by
`AuthenticationCaptureCircuitHandler` on every connect — an expired session stays expired until a reload),
`HttpContext` = the current request's cookie, and "No circuit services and no HttpContext" = the call was made
outside both (e.g. a timer created in a CircuitHandler callback — see "Blazor circuit and JS interop rules").
A Map "Circuit services not available" Warning means the framework stopped wrapping component init in inbound
circuit activities (re-check `ServicesAccessorCircuitHandler` after runtime upgrades).

### "Build fails with file locked errors"

**Cause:** Running services lock DLL files
**Solution:** `taskkill /F /IM dotnet.exe`

### "User authenticated but has no roles"

**Cause:** TenantUser not approved or permissions not set
**Solution:** Admin must approve user and assign permissions in admin panel

---

## Recent Changes

### 2026-10-11: Storage quota charges the change on disk; stale client tokens summarized; marker/food icon fallbacks; ImageSharp comments corrected

**From the 2026-10-10 export (plan step 6, part B: P3-E, P3-B phase 1, P3-C, P3-H).** The verification
run found haggis +2.03 MB and fineroe +1.37 MB tracked above disk — grid refreshes overwrite the same
`grids/{id}.png` every 30 min but were charged the full size each time; one tenant's two deleted tokens
produced 1,040 Warnings in 48 min (5 per request); 48 wellspring markers and 23 food icons 404'd. No
migration. **The /client status codes and JSON bodies are unchanged** (both 401 bodies byte-identical;
gridUpload's 413 body still reports the upload size).
- **Storage quota (P3-E).** `CurrentStorageMB` is now kept by single atomic delta UPDATEs
  (`IncrementStorageUsageAsync` = one `ExecuteUpdateAsync`, `x = x + d` clamped at 0; `Decrement`
  delegates). The old read-modify-write saved a possibly stale tracked tenant + delta: token validation
  tracks the tenant per request, `ZoomTileRebuildService` tracks every tenant for its whole run, and
  `TenantRepository.UpdateAsync` (superadmin rename, provisioning) wrote every column back — each lost
  concurrent deltas. **Contract change:** the increment no longer calls SaveChanges, so it neither flushes
  the caller's pending changes nor refreshes a tracked tenant (every caller already saves first).
  `CheckQuotaAsync` reads the row AsNoTracking; `TenantRepository.UpdateAsync` never writes
  `CurrentStorageMB`/`LastActivityAt` (attaches only when detached).
  - **Charged as the change on disk.** New `Services/AtomicFile` (`WriteAllBytesAsync` = LargeTileService's
    temp + rename, moved verbatim; `LengthOrZero`) and `ITileService.ReplaceTileFileAsync(tenant, path,
    bytes)` (old size, atomic write, charge new − old). Used by **GridUpload** (refresh/winter replacement:
    a same-size refresh charges 0) and **`UpdateZoomLevelAsync`** (every legacy zoom rebuild: merge, wipeTile,
    setCoords, CleanupMultiIdGrids, the dirty rebuild — whose row-based old-size compensation, which missed
    orphan files, is deleted). **GridUpload no longer deletes the tile after a failed step** (that catch
    deleted the only copy of a refreshed tile and left its row dangling); a failed write leaves the old
    file intact and the request answers 500 as before. `ZoomTileCache` (hmap + public import zoom 1-6)
    subtracts the bytes its flushes overwrite.
  - **Merges:** the retired-directory teardown moved from `GridService` to
    `ITileService.DeleteRetiredMapDirectoriesAsync(tenant, map, gridStorage, rehomedBytes)` (same
    live-reference guard; its "Kept directory of merged map" Warning now logs under the **TileService**
    category): one update = bytes the re-home copied into the pool − the per-map directory's bytes, credited
    only when the delete succeeds. **`tenants/{t}/large/**` is reconciled-only** — WebP writes never
    charged, deletes never credited (every delete path is followed by uncharged regeneration).
  - **Quota gate on growth:** gridUpload checks `max(0, new − existing file)`, so a tenant at its quota can
    keep refreshing grids it has; a tenant already over quota is still blocked (current + 0 > quota). A new
    grid's growth = its size, so the 413 cases for new ground are unchanged.
  - **Reconciliation is the authority.** `ReconcileStorageUsageAsync` reads the previous value fresh, scans
    `tenants/{t}/`, sets the absolute value with ExecuteUpdate, syncs a tracked copy (left unmodified), writes
    `.storage.json` and logs one Information line `RecalculateStorage: Tenant … Files, Size, Tracked, Drift`.
    `RecalculateStorageUsageAsync` (the admin teardowns, MapDeletionService included) delegates and **no
    longer warns** — the old "discrepancy detected" Warning is gone (saved queries!).
    `UpdateStorageFromCalculationAsync` and the verification job's duplicate scanner are deleted.
    `TenantStorageVerificationService` loads ids only and warns through the pure **`StorageDrift`**
    classifier, sign-aware and quota-relative: `Storage overcount for tenant …` when tracked − disk >
    max(2 MB, 0.5% of quota), `Storage undercount …` when < −max(10 MB, 2%); its completion line counts both
    and names the largest drift each way. Keys in Api `appsettings.json`: `StorageVerification:IntervalHours`
    (6), `StartupDelayMinutes` (60, was a fixed hour), `OvercountWarnMinMB` (2),
    `OvercountWarnPercentOfQuota` (0.5), `UndercountWarnMinMB` (10), `UndercountWarnPercentOfQuota` (2).
- **Stale client tokens (P3-B phase 1, logging + metrics only).** One static **`ClientToken`** (`Hash`,
  `IsCanonicalSecret` = 64 lowercase hex, `TrySplit` = first underscore at index > 0) replaces the hash
  copies in TokenService, the middleware and TokenMigrationService. **`ClientTokenValidator`** (scoped,
  Services) is the middleware's query moved unchanged (IgnoreQueryFilters, no cancellation) returning
  `ClientTokenValidation(IsValid, Rejection, TokenHash, TenantExists)` — reasons `MalformedToken`,
  `EmptySecret`, `NotFound`, `Expired`, `TenantInactive` (`Core/Enums/ClientTokenRejectionReason`); it never
  logs. **`ClientTokenRejectionTracker`** (Api singleton, `TimeProvider`): the first sighting of a
  (tenant, token hash) → one Warning `Game client of tenant {TenantId} sends a rejected token ({Reason}) on
  {Route} …`, then `… still sends a rejected token ({Reason}): {Count} requests since {Since}, last on
  {Route}` once per `SummaryInterval` (15 min, decided on the next request); idle 24 h → forgotten. Only an
  existing tenant with a canonical secret is attributed, at most 16 hashes + one overflow entry per tenant
  (`Game clients of tenant … more than 16 distinct rejected tokens …`); everything else (malformed, empty,
  non-canonical, unknown tenant) is pooled per reason with **no tenant name** (`N game-client request(s)
  rejected (…) … (not attributed to a tenant)`), so random prefixes can neither grow it nor plant
  tenant-named Warnings. **No log line carries the token, its hash or the IP**; the route is the endpoint
  template (`/client/{token}/positionUpdate`). Per request: Debug + the fixed-value tag
  **`hnhmapper.client_token.rejection`** (`malformed|empty_secret|not_found|expired|tenant_inactive`) on
  `http.server.request.duration` via `IHttpMetricsTagsFeature` — exact counts live there. Options section
  `ClientTokenRejections` (`SummaryInterval`, `IdleExpiry`, `PerTenantCap`; defaults in code). State is
  per process (a restart = one new first sighting). The plan's Serilog Items-flag demotion was not needed:
  step 2 already keeps 4xx out of the request log. Replay of the incident: 1,040 Warnings → 3.
  **`TokenMigrationService` fixed:** it hashed the full `{tenant}_{secret}`, so a migrated legacy token
  could never validate (no-op in production: every token already has a prefix).
- **Icons (P3-C).** Game markers fall back to `gfx/terobjs/mm/custom.png` like every other icon surface:
  `marker-manager.js` `GameMarkerIcon` (an `L.Icon.extend` whose `createIcon` sets `onerror`, always
  overwritten so the glow swap's reused IMG is harmless), an inline `onerror` on the timer `<img>`, and
  `Image=''` goes to the fallback with no request (`/.png` before). **No failed-URL memo** on purpose:
  onerror cannot tell a 404 from a blip and JS state survives circuit resumes. New pure
  `Core/Cookbook/ShippedIcons` (FrozenSet, Ordinal; `Unknown` = may contain anything) built once at Web
  startup by `Services/ShippedIconScanner.Scan(WebRootPath)` (every PNG under `wwwroot/gfx`; missing,
  unreadable or empty → Unknown; startup line `Shipped icon manifest: N icons`). **`FoodIcons` moved to
  `Core/Cookbook`**: `Src(shipped, name)` = local when it ships (or Unknown), the renderer
  `https://www.havenandhearth.com/mt/r/{name}` when it is known not to, `""` for an unusable name (no
  request); `RemoteFallback` is null when src already is remote; `RemoteFallbackOrHide` only hides then.
  Cookbook.razor / NotificationCenter.razor inject `ShippedIcons ShippedIconSet` (not `Icons` — MudBlazor's).
  Browser notifications use `/favicon.png` (the `/favicon.ico` they pointed at never shipped); `App.razor`
  declares the favicon `image/png` (was gif). New **`wwwroot/robots.txt`**: only `/public/{slug}` pages
  allowed (`Allow: /public/`, `Disallow: /public/*/` — tiles/info/markers — and `Disallow: /`).
  **`gfx/terobjs/mm/wellspring.png` is still missing** — it has to be taken from the game client's res
  cache (not fetched or drawn here); until then wellsprings show `custom.png`.
- **ImageSharp comments (P3-H, docs only)** in both `Program.cs`: buffers ≤ 1 MiB — every 400x400 tile
  frame (640,000 B → a 1 MiB array) — come from `ArrayPool<byte>.Shared` on the managed LOH and are
  bounded by neither setting; `MaximumPoolSizeMegabytes` caps only the unmanaged pool (> 1 MiB);
  `MaxDegreeOfParallelism` is per operation; `AllocationLimitMegabytes` is per allocation. The 2026-08-23
  memory-work bullet below is corrected the same way.
- **Tests (767 + Web 50, +97):** `StorageQuotaServiceTests` (14, temp-file SQLite, two contexts: stale
  request-scope and `GetAllAsync` snapshots keep concurrent deltas, clamp, unknown tenant, zero no-op, no
  flush of pending changes, 8 concurrent scopes lose nothing, check reads the row, growth gate at/over quota,
  reconcile fresh previous + tracked copy Unchanged + missing dir + unknown tenant), `TenantRepositoryTests`
  (2), `StorageDriftTests` (11), `AtomicFileTests` (4), `TileServiceStorageAccountingTests` (5: size-change
  charges, zoom rebuild twice = bytes on disk, orphan zoom file not double-counted, retired dirs credit
  legacy only, live reference keeps the dir), `GridServiceMapMergeTests` +1 and `HmapImportServiceMergeTests`
  +1 (quota delta == non-WebP disk delta; both fail with the fix reverted), `ClientTokenTests` (15),
  `IconNamesTests` (1), `ClientTokenValidatorTests` (11 incl. the migration fix), `ClientTokenRejectionTrackerTests`
  (9: session-B replay = First + one Summary of 113, cap/overflow, pooling, prune, concurrent prune),
  `ShippedIconsTests` (3), `ShippedIconScannerTests` (5), `FoodIconsTests` (15).
  `GridServiceMergeImportedTilesTests` now asserts the "Kept directory" line on the TileService logger.
- **Not verified live:** the marker fallback and robots.txt in a browser (no JS harness) — dev check: a
  marker with a missing image shows custom.png with no "Marker" text; `/robots.txt` is 200 text/plain;
  `/cookbook` makes no `/gfx/invobjs` 404s.
- **Ops:** saved queries on `discrepancy detected`, `Token validation failed`, `Invalid token for tenant` or
  the GridService "Kept directory" category need updating. Expect after deploy: per-tenant `RecalculateStorage
  … Drift` lines ≈ 0 for refresh-heavy tenants, no overcount Warnings; 1 + ⌊minutes/15⌋ Warnings per stale
  client token. Ship `wellspring.png` from the game client (and list other missing marker images from a prod
  DB copy); check the live Caddyfile's CSP still allows `https://www.havenandhearth.com` in `img-src`.

### 2026-10-11: ZoomQ rebuilds each tile once per batch; revision bumps name their cells; reconnect tile deltas from the DB; no exceptions as control flow

**From the 2026-10-10 export (plan step 6, part A: P3-D, P3-G, P2-A step 3, P3-F).** ZoomQ rebuilt the z0 cell and
all six ancestors per uploaded grid (7 rebuilds per upload where 2.5-4 tiles change; 392 batches, p50 1.8 s);
every revision bump made every viewer refetch every tile in view (16-19 requests per viewer per bump, 72% of map
2813's tile requests); `TileCacheService` reloaded a whole tenant's tile list 168 times in 75 min to answer
0-6-row `since` deltas; and 392 of the API's 681 `OperationCanceledException`s were ZoomQ's debounce timer, plus
one JsonReaderException + JsonException per Hurricane marker post. One migration (`AddTileTenantCacheIndex`).
- **No exceptions on normal paths (P3-D).** `ZoomTileQueueService.ReadBatchAsync(window, ct)`: wait for the first
  request, a plain `Task.Delay(window)`, then a non-blocking `TryRead` drain — the processor used a cancelled
  `ReadAsync` as its 500 ms timer (one exception per batch). `LenientJson.Deserialize<T>(json, options, out
  repaired)` quotes bare tokens FIRST and parses once (`QuoteBareTokens` now copies spans and allocates its builder
  only at the first bare token, so a valid body costs one scan); `ClientEndpoints.ReadMarkerListAsync` uses it
  with the shared `CaseInsensitiveJsonOpts` (was a new options object per call) and logs repairs at Debug. The
  /client marker endpoints answer exactly as before (same parse results, same 400 path). **Rules: never end a
  wait by cancelling it on a normal path; `dotnet.exceptions` counts every async rethrow under the SHORT type name
  (`JsonReaderException` is System.Text.Json's internal type, no Newtonsoft) — one escaped exception can show as
  10-15.**
- **Distinct tiles per batch (P3-G).** Pure `Services/ZoomTileBatchPlanner` (requests grouped by zoom-0 cell =
  `WebpDirtyCellPlanner.CellOf`, then distinct parents level by level z1..z6, bottom-up; parents always planned —
  the 2026-08-22 "walk parents even when z0 is null" ghost fix) + `ZoomTileBatchRegenerator` (Api singleton): each
  planned tile `ForceRegenerate`d once, every z0 cell before any parent, so each parent composites once from fresh
  children; a throw is caught per tile and the batch goes on. Generated/Failed stay per request (= its cell
  produced bytes). A 3x3 neighbourhood: 63 rebuilds → 10 (aligned) / 28 (at an origin). The processor's dead
  `DistinctBy` is gone (the queue never holds duplicates).
  - **Key release just before the read.** `ZoomTileQueueService.BeginProcessing` (called for a cell's requests
    right before its z0 read) releases the dedup key, so an upload landing during the batch is queued for the next
    one instead of being skipped behind a read that already happened (it used to be released after the request's
    whole chain); `EndProcessing` (batch end) never touches a re-enqueued key; requests a cancelled batch never
    reached are released with `MarkCompleted`. `IsQueued` = pending OR in flight until the batch ends, so the
    WebP backstop's attempt accounting (`ProcessedAttempts`) still counts an attempt only once its whole chain
    ran. **Deviation:** the backstop now checks `IsQueued` before enqueueing — with the key released early, the
    enqueue alone would accept a duplicate of an in-flight cell (and forget the in-flight attempt).
  - Logs: `Processing batch: N requests -> C z0 cells, T tiles` (Debug); `Batch complete: … in Ms ms (T tiles,
    C z0 cells for N requests; revisions 2813@115:3, …)` — `@rev:cells`, or `:all` over the cap. Saved queries on
    the old text need adjusting. The ZoomQ `TileCacheService` invalidation is deleted: the batch writes WebP
    files, never Tiles rows. Web invalidation and the per-map revision bump are unchanged. **Review fix:** the
    Tiles writers that had silently relied on it (their cells reached ZoomQ) now invalidate themselves —
    `GridService.MergeMapsAsync` (GridService takes the `TileCacheService` singleton) and both imports
    (`TenantAdminEndpoints.EvictImportedMapTileCachesAsync`); without it a page load after a merge/import got
    the pre-change token list, and rows below the client's later `since` marker never arrived.
- **Revision bumps name their cells (P2-A step 3).** `MapRevisionDto.Cells` (`int[][]?`, `[x, y]` zoom-0 cells,
  `JsonIgnore WhenWritingNull`); new **overload** `IUpdateNotificationService.NotifyMapRevision(mapId, revision,
  changedCells)` (not an optional parameter — Moq expression trees, CS0854; the two-argument one = null = whole
  map, used by every other writer); `ZoomTileCells.ChangedCellsByMap(requests)` (distinct cells per map, null over
  256). `WatchGridUpdates` serializes the DTO: `{"mapId","revision"}` byte-identical without cells,
  `"cells":[[x,y],…]` with them; the connect replay never carries cells. Client: map-updates.js passes `cells` to
  `window.hnhMapper.setMapRevision(mapId, revision, cells)`, which honours them **only when the revision is the
  held one + 1** (`MapRevisionCache` steps by 1; anything else = a missed bump → full refresh, as for every bump
  without cells, the .NET fallback and seeds). `smart-tile-layer.js` keeps pending `all` / `cells` per layer
  (union across debounced bumps; >256 or malformed → full); a narrowed run refreshes only the tile nodes (every
  retained level) whose server coords are a changed cell or one of its ancestors (`x >> z`), deletes only those
  negative-cache entries (also out-of-view ones: a newly mapped area's cached 404 must not hide it) plus the retry
  set's (a failed refresh negative-caches its tile, which would skip the retry). Hidden-tab coalescing and the
  failed-tile retry set are kept. **Deviation:** runs no longer supersede each other (a narrowed run covers only
  its cells, so cancelling an earlier run's remaining batches would lose them): one shared per-layer key queue,
  drained by a single 4-per-frame loop. Old JS ignores `cells` (full refresh); new JS against an old API gets no
  cells (full refresh) — no deploy-order constraint. **Review fix — placeholders:** a node whose first load
  failed (createTile's error path hides it) or that `getTileUrl` skipped while negative-cached shows
  `L.Util.emptyImageUrl` and was only ever re-requested by the old every-bump full refresh, so a transient 503
  stayed a hole. Narrowed runs now also queue every placeholder node (`_isPlaceholder`) but keep its
  negative-cache entry, so `_refreshTile` re-requests it only once the 30 s entry has expired (a lasting 404 =
  one request per TTL, not per bump); a failed placeholder refresh stays out of the eager retry set (which
  would clear the entry every bump).
- **Reconnect tile deltas from the DB (P3-F).** `TileCacheService.GetTilesSinceAsync(tenantId, since)`:
  `since > 0` (every reconnect) = `ITileRepository.GetTileCacheTokensAsync` (projected `TileCacheDto`,
  `IgnoreQueryFilters` + explicit TenantId) — never touches or loads the snapshot, and sees every committed writer;
  `since <= 0` (page load) = a per-tenant `Lazy<Task<Snapshot>>` of projected DTOs (one shared load via `Task.Run`;
  a failed load is removed by value-matched `TryRemove`; an invalidation during a load cannot resurrect it). The
  process-wide `SemaphoreSlim`, the unsynchronized `Dictionary` and the `HttpContext.Items["TenantId"]` write are
  gone; `GetAllTilesAsync` (service and repository) is deleted. Invalidation is a lock-free removal (gridUpload,
  wipeTile, setCoords, merges, both imports and the superadmin paths call it — it only keeps page loads
  fresh, so every writer of Tiles rows must); "Loading tiles"
  and per-tenant "Invalidated" lines are Debug, "Loaded N tiles" stays Information (~one per page load after an
  upload). New index **`IX_Tiles_TenantId_Cache`** (migration `20261011023704_AddTileTenantCacheIndex`,
  CLI-generated, one `CreateIndex`): the delta is O(log n + changed rows) — without it a 58k-68k-row tenant took
  24-30 ms per reconnect on the dev copy. `SseWireContractTests` now looks for `GetTilesSinceAsync(` after the
  subscriptions.
- **Tests (670 + Web 50, +60):** `ZoomTileQueueServiceTests` +9 (window collects, waits for the first item, next
  batch, no first-chance exception on window expiry — filtered to the test's async flow, plus a detector test
  proving the old CancelAfter pattern IS caught — shutdown while waiting and inside the window,
  Begin/EndProcessing), `ZoomTileBatchPlannerTests` (10: 3x3 blocks 10/7/28 tiles, single request = the old loop's
  chain incl. negatives, grouping, tenant/map separation), `ZoomTileBatchRegeneratorTests` (8: once per tile and
  z0 first, null z0 still builds parents, key released just before the read and kept in flight, throw / failed
  scope / cancellation release everything), `LargeTileServiceTests` +1 (real service: a stale opaque sibling never
  reaches the parent, each tile built once), `ZoomTileCellsTests` (11), `UpdateNotificationServiceTests` +2,
  `MapRevisionDtoWireTests` (3, incl. the Moq overload guard), `LenientJsonTests` +6 (no first-chance
  JsonException, span bookkeeping, strict-parse equivalence, broken JSON still throws), `TileCacheServiceTests`
  (10, temp-file SQLite `Pooling=False`, ambient tenant pointing elsewhere: tombstones, strict delta, cached until
  invalidated, delta never reloads, cold delta loads nothing, 20 concurrent reads = 1 load, failed load not
  cached, invalidate-during-load, tenant scoping, EXPLAIN QUERY PLAN of the captured delta query uses the index).
  Review fixes (after part B, suite 769): `GridServiceMapMergeTests` +1 (a merge drops the page-load snapshot, a
  single-map gridUpdate keeps it), `WebpDirtyCellBackstopTests` +1 (a cell between Begin/EndProcessing is
  neither re-enqueued nor counted as an attempt; gives up only after MaxAttempts of the backstop's own). Both
  fail with their guarded line removed.
- **Not verified live:** the JS changes (no browser run; throwaway stubbed-Leaflet harnesses only, deleted) —
  dev check: DevTools EventStream shows `"cells"` on `mapRevision` after an upload, and `/map/tiles` refetches
  only the changed tiles' chain (plus placeholders whose 30 s negative entry expired) ~500 ms later; a
  wipeTile/setCoords bump or a reconnect gap still refreshes the whole view.
- **Ops:** back up grids.db (the index builds at API start, a few seconds on ~0.5M rows). Expect `Batch complete`
  tiles/requests ≈ 1-3 instead of 7, "Loading tiles from database" ≈ page loads after uploads (was 168 / 75 min),
  and API `OperationCanceledException` ≈ real SSE disconnects only.

### 2026-10-11: WebP pyramid gets its own durable queue; merge double-claims rejected; one map-delete teardown; missing-source repair

**From the 10-10 integrity scan (27 drifted maps, 4 maps with contested cells, 26 tenants with orphan
storage) and silkcloth's dark map 2813.** Two plan items (P2-C + P1-A PR-C), one migration
(`AddDirtyWebpCells`). Suite 583 + Web 50; 610 + Web 50 after the same-day review fixes at the end.
- **New `DirtyWebpCells` table** (migration `20261011011549_AddDirtyWebpCells`, CLI-generated, no seed
  inside it): one row per 4x4 WebP zoom-0 cell (`TenantId, MapId, CellX, CellY, MarkedAt`; unique
  `(TenantId, MapId, CellX, CellY)`, index `(TenantId, MarkedAt)`, FKs to Tenants and Maps with cascade
  — deleting or merging away a map drops its marks; no tenant query filter, every query is explicit).
  **Why:** the old backstop derived cells from zoom-1 `DirtyZoomTiles` rows, which the legacy PNG
  rebuild (`ZoomTileRebuildService`) deletes on its own timer — two consumers of one durable queue, so
  the share the WebP side saw swung 0-100% with timer phase; `CreatedAt` never moved forward, so a
  second import flush into a cell regenerated between flushes looked fresh; and freshness was judged
  on the z0 file only. **Rule: never feed a durable queue to two consumers — give each its own.**
  - Written by BOTH dirty funnels in `TileService` (per-tile `MarkParentTilesDirtyAsync` on every
    zoom-0 save, and `MarkParentTilesDirtyBatchAsync` for imports/merges/rebuilds), after the tile rows
    are saved; `MarkedAt` is **bumped on re-mark**; floor division for negatives; own SaveChanges in
    500-cell chunks with one retry (a concurrent insert, or the backstop deleting the row between our
    read and update), separate from the DirtyZoomTiles save.
  - **Single consumer `Services/WebpDirtyCellBackstop`** (scoped), run per active tenant from
    `LargeTileGenerationService`'s 5-minute dirty scan in its own try/catch — no longer behind the
    `HasDirtyZoomTiles` fast skip, and a failing gap-fill no longer skips it. Reads marks older than the
    **5-minute settle** (the fast path owns younger ones), oldest first (5,000 per run). A mark is
    consumed only once the cell's whole EXISTING z0..z6 chain is newer than `MarkedAt` and complete
    (missing parents are fine; a missing z0 needs regeneration only when a row's source PNG exists);
    otherwise the cell is enqueued on the ZoomQ fast path up to the shared headroom
    (`ZoomTileQueueService.BulkEnqueueDepthLimit` = 3000, now one constant for every bulk producer;
    the rest is deferred, its mark kept). Deletes are conditional (`MarkedAt <= the run's cutoff`), so
    a re-mark that lands mid-run survives. Two guards against loops: a **dark or partly dark cell**
    (all, or only some, of its rows' PNGs missing — classified per row) is consumed without
    regenerating — a regeneration would REGEN-EMPTY-delete (dark) or redraw without those rows (partly
    dark: the zoom-0 compose skips missing sources) the last WebP copy of what they showed; and a mark
    still stale after **3 regenerations the processor actually handled** (counted per process; a
    request DropOldest evicted unprocessed does not count — see the review fixes) is dropped with one
    `BACKFILL [...] gave up on N cell(s)` warning (the drift scan reports the rest). One `[LargeTile] BACKFILL [tenant] enqueued N, cleared M, deferred K, gave up G` line per
    tenant with work. `EnqueueStaleWebpCellsAsync` and the zoom-1-row `WebpDirtyCellPlanner.DeriveCells`
    are gone; the planner is now pure `CellOf` / `Chain` / `InspectChain` / `NeedsRegeneration`.
  - **Seeded at runtime, not in the migration:** once per process start (before Phase 1, retried each
    cycle until it succeeds) `SeedFromLegacyDirtyRowsAsync` runs `INSERT OR IGNORE INTO DirtyWebpCells
    … SELECT TenantId, MapId, CoordX >> 1, CoordY >> 1, MAX(CreatedAt) FROM DirtyZoomTiles WHERE
    Zoom = 1 … GROUP BY` (SQLite's `>>` is an arithmetic shift = floor for negatives, verified;
    dead-map rows skipped because OR IGNORE does not cover foreign keys). Idempotent: an existing mark
    never moves, re-seeded already-fresh cells settle without an enqueue. Nothing pending at deploy is lost.
  - **Gap-fill refreshes existing parents** of every tile it generated (it used to skip any existing
    file, producing exactly the drift scan's "older than child" state); existing tests' counts went up
    accordingly (a restored cell now rewrites its z3-z6 ancestors too). Every tile it writes is evicted
    from the process caches and the touched maps are published (Web cache + map revision) — see the
    review fixes; without that the refresh was undone by ZoomQ.
  - `TenantDataPurgeService` purges `DirtyWebpCells` (`PurgeTenantDataResultDto.DirtyWebpCells`).
- **Merge preflight (`GridService.ProcessGridUpdateAsync`):** before the insert loop, if an eligible
  merge would put a shifted source grid on a target cell owned by a different grid id — the target's
  stored grids, the unknown grids this matrix is about to plant, or a previously checked source — the
  whole update is rejected: `GridUpdate REJECTED … merging map(s) X into Y would double-claim N
  cell(s), e.g. …` + the empty response, mirroring the conflicting-offsets guard. Merges were the only
  Grids writer without an occupied-cell check, and both maps' witnesses being internally consistent
  hides a frame mix from the offset guard (the 2242/2448/2572 contested cells). A source's own
  pre-existing duplicates are not counted against itself. A legitimate merge never double-claims.
  The verdict is remembered for 5 minutes (see the review fixes).
- **One map-delete teardown `IMapDeletionService.DeleteMapAsync(tenantId, mapId)`**, used by the
  tenant-admin `DELETE /admin/maps/{id}` and the superadmin `DELETE /api/superadmin/tenants/{t}/maps/{id}`:
  IgnoreQueryFilters + explicit tenant; one transaction child-before-parent (dirty marks, markers on its
  grids, custom markers, roads, pings, overlays + offsets both ways, tiles of all zooms, grids, map);
  then files — each deleted only when no LIVE-map tile row references it (both separators; the
  merge/scan/purge rule; containment-checked to the tenant dir), the per-map dir unless a live row
  points inside it, and `large/{id}` via `DeleteMapWebpTiles`; `RecalculateStorageUsageAsync` last.
  **The superadmin delete removed only the Maps row before** (Tiles/Grids have no FK to Maps — they
  stayed on the dead id and the next gridUpdate re-anchored to it); the tenant-admin delete never
  removed `large/{id}` (58 dead trees in the 09-18 copy) and deleted files other maps still used. Both
  endpoints also evict the Web cache (`SuperadminEndpoints.InvalidateWebMapTileCacheAsync`, now
  internal) and both delete buttons post through `APIUpload` (file deletion can pass the default
  client's 10 s timeout, whose retry would 404). `HmapImportService.CleanupFailedImportAsync` also
  deletes `large/{id}` and recalculates the quota instead of decrementing only the PNGs it found.
- **ZoomQ DropOldest no longer leaks dedup keys:** the bounded channel's `itemDropped` callback
  releases the dropped request's key and counts it; at most one `[ZoomQ] Queue full: dropped N …`
  warning per minute. Before, a dropped base coord could never be enqueued again and `PendingCount`
  (every producer's headroom gauge) grew until a restart. `EnqueueZoomRegeneration` now returns
  whether it queued anything (the backstop counts attempts by it).
- **Missing-source repair (P1-A PR-C):** `POST /api/superadmin/integrity/tenants/{t}/repair-missing-sources`
  (`{confirmTenantId, forgetUnrecoverable=false}`), re-derived server-side
  (`MapIntegrityService.RepairMissingTileSourcesAsync`): a missing row whose cell has exactly one owner
  grid with a pool PNG is repointed to it (File, size, Cache=now); the owner of a row without one is
  re-armed (`NextUpdate` in the past); contested cells are skipped; rows with no owner stay. **Restore
  keys** (`map M: tenants/t/N/0 (dead map) shift=(sx,sy) rows=R bytes=B`) are computed before anything
  changes and written in the `SuperAdminRepairedMissingTileSources` audit's OldValue **before** the
  opt-in forget (`ForgetMissingTileSourceRowsAsync`) deletes the unrecoverable rows — re-checking each
  file first, so a file restored meanwhile keeps its row. Map Integrity → "Missing tile sources" has
  per-tenant **Repair (non-destructive)** and **Forget unrecoverable rows** buttons (counts-explicit
  confirms, `APIUpload`, rescan). `GridUpload`'s `AllowGridUpdates=false` guard now blocks only when
  the existing row's file exists (`ITileService.HasRenderableTileAsync`), so a re-armed grid can heal
  by re-walking on no-updates tenants; the client protocol is unchanged.
  - **Deviation from the plan:** repointed cells are regenerated cell by cell (marks + fast queue +
    both processes' map caches evicted), NOT with `RebuildMapWebpCoreAsync` — that deletes the map's
    whole pyramid first, including the last WebP copy of areas that are still dark.
- **Tests (+34):** `WebpDirtyCellBackstopTests` (9, real SQLite: legacy consumer runs first and the
  cell is still enqueued then cleared once fresh; multi-flush re-enqueue; re-mark survives the
  conditional delete; settle window + other tenants untouched; dark cell keeps its WebP; give-up after
  3 attempts; headroom deferral; seed floors negatives / takes the newest mark / is idempotent; map
  delete cascades the marks); `WebpDirtyBackstopTests` (marking: negative floors, re-mark bump,
  zoom-0-only; planner: whole-chain freshness, missing z0, zero-length, dark); `ZoomTileQueueServiceTests`
  (3); `GridServiceHardeningTests` +3 (double claim with a stored grid, with a second source, with a
  grid the matrix plants — all rejected before any write); `LargeTileServiceTests` +1 (parent refresh);
  `MapDeletionServiceTests` (4, real SQLite + temp dir, incl. no orphans in a rescan);
  `MapIntegrityServiceTests` +4 (repair non-destructive, forget only still-missing unrecoverable rows,
  unknown tenant, repair makes the grid requestable through `ProcessGridUpdateAsync`);
  `TileServiceRenderableTileTests` (1); purge test asserts `DirtyWebpCells`.
- **Ops:** back up grids.db (one migration); after deploy expect a `BACKFILL seeded N` line, then
  `BACKFILL` lines after imports/merges/restarts and `DirtyWebpCells` small at steady state;
  `GridUpdate REJECTED … would double-claim` lines are prevented corruption (frequent ones from one
  tenant point at a client bug); `[ZoomQ] Queue full` should stay absent. The Global Maps delete
  button is safe to use now. Order for dark maps: restore files first if a backup has them, then
  Repair (non-destructive), and Forget only once backups are exhausted.
- **Review fixes (same day, 10 findings).** Suite 583 → **610** (+27), Web 50, no new warnings, no migration.
  - **Partly dark cells are never redrawn by the repair or the backstop.** Regeneration is per 4x4 cell
    and `GenerateZoom0LargeTileAsync` silently skips rows whose file is missing, so redrawing a cell
    that holds both a repointed (or present) row and a still-missing one overwrote the z0 — and, via
    ZoomQ, z1-z6 — without the missing row: the last WebP copy of it, exactly what the dark-cell rule
    protects. `CellSources` gained **`PartlyDark`** (`ClassifyCellSourcesAsync` counts present files
    instead of `Any`), and `NeedsRegeneration` is false for Dark AND PartlyDark. The repair marks every
    repointed cell durably but enqueues only cells with no row still missing
    (`MissingTileSourcesRepairResultDto.RedrawBaseCoords`); the others are **`CellsHeldForRestore`**
    — the backstop redraws them once nothing in them is missing (a forget in the same click, or a
    restore within minutes), later than that the drift scan flags them stale for a rebuild.
    `RegenerateWebpCellsAsync` now takes `markCoords` and `redrawCoords` separately.
  - **Rebuild-all skips** maps listed under missing tile sources (its full-pyramid delete would take
    their dark areas' last imagery with it; `RebuildAllDriftResultDto.SkippedMissingSources`, warning
    snackbar) and maps whose only drift is empty files (`SkippedEmptyFilesOnly` — serving and the
    batch regenerate those). The per-map rebuild stays available, but its confirm now names the
    map's missing rows and dark/partly dark cells and reads "Rebuild anyway"; the rebuild-all confirm
    counts what it will rebuild and lists what it skips.
  - **Forget keeps every row rebuildable.** The restore keys are per (map, directory) aggregates, so
    a forgotten row in a `shift=mixed` directory, or a pool-path row no grid owns, lost its
    coordinate-to-file mapping. The repair now lists those rows one by one
    (`UnkeyedRows`: `map M (x,y) {file} bytes=N`), and a forget writes them into the audit entry
    before deleting anything. Single-shift per-map directories and pool PNGs named after their
    cell's owner grid stay key-only.
  - **Path-like grid ids.** Grid ids are client strings that become pool file names, and
    `ValidatePath` only keeps a path inside the tenant: `"../5/0/1_1"` names another map's file. New
    `TenantFilePathService.IsSafeGridId` (no separators, `..`, `:` or invalid file-name chars) /
    `PoolRelativePath` (null when unsafe); `GetGridRelativePath`/`GetGridFilePath` throw
    `UnauthorizedAccessException` for one (the merge re-home calls them inside its try → CopyFailed).
    `ProcessGridUpdateAsync` treats such ids as holes (one warning with the count), `gridUpload`
    answers 400, the scan/repair classify an unsafe owner as "no grid" (never repointed or re-armed),
    and `MapDeletionService` builds no pool candidate for one and compares candidates and live
    references in **canonical form** (`..` resolved) — the raw string compare let
    `tenants/t/grids/../2/0/0_0.png` delete a file a live map's row named `tenants/t/2/0/0_0.png`.
  - **Gap-fill writes reach every cache.** The parent refresh rewrote z1-z6 files without evicting the
    static `_tileCache`; ZoomQ's `ForceRegenerate` composes children through that cache, so the next
    upload in the footprint baked the stale cached sibling back into z2-z6 with a fresh mtime (the
    backstop had already settled the mark, and the derivation rule cannot see it). Now every tile the
    batch writes is `InvalidateCacheEntry`'d (positive + negative), `GenerateMissingTiles[ForMaps]Async`
    report the maps they wrote (`writtenMapIds`), and `LargeTileGenerationService.PublishWrittenMapsAsync`
    evicts the Web cache per map (stops at the first unreachable Web — startup) and bumps + broadcasts
    the map revision, as ZoomQ does. Both import endpoints also evict both processes' caches of the
    affected maps when they finish (a new area's cached 404 would otherwise outlive the import).
    `SuperadminEndpoints.InvalidateWebMapTileCacheAsync` now returns whether the Web was reached.
  - **Backstop attempts count only processed regenerations.** An attempt was counted on enqueue, so a
    request DropOldest evicted still used one of the 3 and a cell could be given up without ever being
    regenerated. The attempt state keeps the queue's `DroppedCount` at enqueue; the attempt counts once
    the request has left the queue (`ZoomTileQueueService.IsQueued`, new) with no drop since. Under
    sustained drops nothing accrues, so the backstop keeps trying. And `GridService
    .EnqueueWebpRegenerationForCells` (merges, wipes) now respects `BulkEnqueueDepthLimit` — 1500 merge
    cells on top of a backstop-filled queue overflowed the 4096 channel.
  - **Merge rejections are remembered** (`Services/MergeRejectionCache`, Api singleton, 5-min TTL, keyed
    by tenant + target + each source with its shift). A double-claim rejection changes nothing, so the
    same frames came back on every gridUpdate of every client walking the area, each time loading the
    whole source map + the target's bounding box and logging the full matrix. A remembered verdict
    rejects without queries (Debug line); the next recomputation logs one WARNING with the count of
    identical rejections it stood for. A repair is seen at the latest after 5 minutes. `GridService`
    takes the cache as a constructor argument (per-test instances — a static would leak verdicts
    across tests that reuse tenant and map ids).
  - Scan DTO `MissingTileSourcesDto` gained `PartlyDarkCells`, `RowsWithoutGrid` and `RepairHeldCells`
    (the repair's classification, shared via `ClassifyMissingRow`), so the panel's repair confirm says
    how many grids are re-armed vs rows that have no grid, and how many cells are held; new "Partly
    dark" column.
  - Tests: `MergeRejectionCacheTests` (3), `TenantFilePathServiceGridIdTests` (14), backstop +2
    (partly dark kept; a dropped request is not an attempt), planner PartlyDark, `GridServiceHardeningTests`
    +2 (repeated rejection = one warning; path-like ids are holes), `GridServiceMapMergeTests` +1 (merge
    at headroom enqueues nothing), `MapIntegrityServiceTests` +3 (scan predicts the repair; only clear
    cells redrawn; path-like owner never repointed/re-armed) and the zoo test asserts held cells and
    unkeyed rows, `MapDeletionServiceTests` +1 (path detours), `LargeTileServiceTests` +1 (batch evicts
    the refreshed parent and the new tile's cached 404, reports the map).
- **Still open:** when the fast path (an upload or a merge enqueue) reaches a dark or partly dark cell,
  `ForceRegenerate` still rewrites a partly dark z0 (and its ancestors) without its missing rows and
  REGEN-EMPTY-deletes a fully dark one — only the backstop and the repair skip them; merges still drop
  source tile rows without a grid row (public-import "Option F" tiles).

### 2026-10-11: Zero-length WebP files and missing tile sources — readers validate, the batch never holes a parent, dark areas are reported

**From the 2026-10-10 export: 9 "Failed to load child tile from file" warnings at API start (an
`ArgumentNullException('pointer')` = a 0-byte file) on 5 maps of 3 tenants (feather-quail 2538 "WORLD",
2604, 2700; sketchbook 2608; hemp 1984), and silkcloth map 2813's batch reporting `failed=471` every
5 minutes with no explanation.** Two problems, one pass over the WebP pyramid's readers.
- **Zero-length/truncated pyramid files (P2-B).** Builds before the atomic writes (ec31a13) overwrote
  tiles in place; a write that failed half-way (most likely a full disk) left a 0-byte file, and every
  reader trusted any existing file: served it as an empty `200 immutable`, composited it as a
  transparent quarter (the decode error was swallowed), or — when it was a parent's only child — read
  "no sources" so that `ForceRegenerate` deleted the valid parent (REGEN-EMPTY), cascading up through
  ZoomQ. The batch skipped any existing file, so it never healed. New **`Services/WebpTileFile`**
  (`IsComplete`: 12-byte RIFF/WEBP header + the RIFF size fits the length — no decode, zero false
  positives on 103,805 prod tiles; `TryReadAsync`: a file that vanished after `File.Exists` reads as
  missing instead of throwing). **Rule: a pyramid file that is not a complete WebP counts as missing
  everywhere.**
  - Serving (`GetOrGenerateCoreAsync`): an incomplete file is discarded (`DiscardCorruptTile` — this
    tile only, one `CORRUPT` warning, skipped if the file was meanwhile replaced) and regenerated through
    the coalesced path; empty bytes are never served or cached. Ancestors are never deleted from the hot
    path: ones composed without the child are caught by the drift scan's derivation rule.
  - Compose paths (on-demand, `ForceRegenerate`, batch `GenerateZoomNFromFilesAsync`) no longer swallow
    child decode errors: `LoadChild` discards an incomplete/undecodable child and throws, so the parent
    fails (503 on view, kept by `ForceRegenerate`, left missing by the batch) instead of a hole or a
    deletion; the next attempt regenerates the child.
  - Batch: `HasUsableTileFile` (stat-only) treats files < 12 bytes as missing, so the same bottom-up pass
    rebuilds them and their missing ancestors; a **per-level failed-coordinate set** means a parent is
    never composed around a failed child (the file compose reads a missing child as empty space —
    previously any child failure was baked into the parent); `GenerateLargeTileWithPreloadAsync` errors
    propagate (they used to read as "empty"); one tile's error is logged with coordinates and no longer
    aborts the tenant. **`BATCH-COMPLETE` now reads `generated= skipped= empty= failed= missingSources=`**
    — `failed` = generation errors (each also an ERROR line) plus the parents left missing above them;
    cells with nothing renderable (dark cells and their parents) are `empty`.
    Any log query parsing it needs adjusting.
  - Drift scan: files shorter than a header count as `CorruptFiles` ("Empty files" column; the daily
    `[IntegrityCheck]` line says `… stale / N empty`); Rebuild-all skips maps whose only drift is empty
    files (they heal on serve and in the batch), and rebuilds the others.
- **Missing tile sources (P1-A PR-B, detection for the merge data loss below).** A "dark" cell has
  zoom-0 rows but none of their source PNGs on disk — nothing can draw it, and a rebuild cannot help.
  - Batch: preload, on-view footprint check and zoom-0 query filter `File != ""` (wipe tombstones are
    not sources — matches the backfill and the rebuild); dark z0 cells are counted, and **one
    `SOURCES-MISSING` warning per map whenever the dark-cell count changes** (static per-process
    throttle, so once per damaged map after each deploy) names the map, cells and rows.
  - Integrity scan: new **`MissingTileSources`** category (`MissingTileSourcesDto`, feeds `IsClean`, the
    daily WARNING — `N map(s) with missing tile source files (rows, dark cells)` — and a Map Integrity
    table): live-map zoom-0 rows whose file is missing, dark 4x4 cells, a per-directory breakdown with
    the merge shift (row coord − file-name coord) and a `(dead map)` marker, rows recoverable from the
    grid pool (single owner grid with a pool PNG) and ambiguous (contested) rows. Source files are
    checked from **one directory listing per directory** (a stat confirms only the misses), not a stat
    per row — a tenant pool holds ~100k PNGs. Dead-map rows stay orphan storage. Report only: the rows
    are the restore key (path + `FileSizeBytes`); nothing deletes or rewrites them (repair = PR-C).
  - Corrected docs: missing zoom-0 WebP files heal on view **only where their source PNGs exist**.
  - Ops: restore the files to the exact paths first, THEN "Rebuild WebP tiles" — a rebuild before the
    restore also deletes whatever the old pyramid still shows of the dark area (the panel says so).
- **Tests (549 + Web 50):** `WebpTileFileTests` (4); `LargeTileServiceTests` +10 (zero-length served →
  regenerated; zero-length without sources → deleted + 404; only-child zero-length rebuilds the parent
  instead of deleting it; zero-length sibling → no hole; batch rebuilds a zero-length z0/z5 tile with its
  missing ancestors; truncated child fails parent and grandparent, next scan heals; z0 write error never
  holes its parent; `SOURCES-MISSING` once + `missingSources=1` + tombstones ignored; tombstone-only
  footprint) — the fake `{1,2,3}` WebP fixture is a real tiny WebP now; `LargeTileServiceQueryCostTests`
  (12, see the 2026-10-10 follow-up); `MapIntegrityServiceTests` +3 (empty files; missing sources with
  dead-dir shift, recoverable, ambiguous, tombstone and dead-map exclusions; all-present = nothing).
- **Not done:** `ForceRegenerate` of a dark zoom-0 cell still takes the "no sources" branch and deletes
  an existing z0 WebP (the last copy of that imagery) — the backstop now never enqueues a dark or
  partly dark cell (see the DirtyWebpCells entry above); the non-destructive repair shipped there too
  (PR-C), and Rebuild-all now skips maps that have missing sources (its review fixes).

### 2026-10-11: Circuit/JS robustness — per-circuit API identity, a poll that never gives up, /map in hidden tabs, no JS after the circuit is gone

**From the 2026-10-10 export: every /map circuit that reconnected got 401s on the marker poll exactly 60 s
later and stopped it for good ("Marker updates stopped"); 2 of 9 map inits hung for the 2-minute interop
timeout in hidden tabs and logged two CRITICAL records each (the map stayed dead until a reload); ~1,600
swallowed Web exceptions came mostly from JS calls made after the circuit was already gone; and every map
load logged a `toggleRoads` JSException.** Rules now in Key Implementation Details → "Blazor circuit and JS
interop rules".
- **Web→API identity (P1-B).** Root cause: `AuthenticationStateCache` is scoped, but
  `AuthenticationDelegatingHandler` is resolved by IHttpClientFactory in ITS OWN handler scope, shared by every
  user for the 2-minute handler lifetime. The handler used the current HttpContext — which a Timer keeps only
  while the circuit's original WebSocket request lives — and otherwise fell back to that shared cache: empty
  after a reconnect (the 401s), or another user's cookie/IP (a latent cross-user/tenant request; the handler's
  writes into that cache were already deleted as a stop-gap before this change). Now Microsoft's "access
  server-side Blazor services from a different DI scope" pattern:
  - `CircuitServicesAccessor` (singleton, static AsyncLocal of a per-circuit `CircuitServicesHolder`) is set by
    `ServicesAccessorCircuitHandler.CreateInboundActivityHandler` around every inbound circuit activity — on
    ASP.NET Core 10 that wraps component init (first UpdateRootComponents, resume), events, JS interop and
    navigation, NOT `OnConnectionUp/Down`. Execution contexts captured inside (the Map timers created in
    `OnInitializedAsync`, Task.Run, continuations) keep it across reconnects; `OnCircuitClosedAsync` clears the
    holder so a stale timer resolves nothing instead of a disposed scope.
  - `AuthenticationCaptureCircuitHandler` (was the misnamed CircuitHandler `CircuitServicesAccessor`) captures
    user, `HnH.Auth` cookie AND `ClientOrigin` into the circuit's own cache on every connect; a connect without
    HttpContext keeps the previous state (it used to wipe the cookie); the dead legacy `HnHMapper.Auth` name is
    gone. Logs: Debug when captured (anonymous included), Warning only for an authenticated circuit without the
    cookie or a connect without HttpContext. Serilog override key renamed accordingly in `appsettings.json`.
  - `AuthenticationDelegatingHandler` is stateless: circuit cache (authoritative, incl. "no cookie" for an
    anonymous circuit, whose origin is still forwarded) → else the current request's HttpContext (SSR,
    prerender, the minimal-API proxies) → else nothing. It owns `Cookie` / `X-Forwarded-For` /
    `X-Forwarded-Proto` (caller values dropped; only `HnH.Auth` is forwarded). **One Warning per failed call**:
    "no circuit services and no HttpContext" (then no 401 line) or "API auth failure" with `AuthSource`;
    the per-request line is Debug. One change fixes all ~170 `CreateClient("API"/"APIUpload")` sites,
    including MapManagement's status poll, GlobalPublicMapsViewer's timer and the long-running import readers.
  - Map's marker poll: one-shot timer re-armed after every tick from `Services/Map/MarkerPollBackoff`
    (2/4/8 s, then 60 s forever; ticks never overlap). Fast retries log Debug; the first slow-mode failure logs
    ONE Warning and shows "Marker updates are failing; retrying every minute." once per streak; recovery logs
    Information + "Marker updates resumed.". No re-arm while disconnected; `HandleCircuitUp` resets the fast
    retries and ticks 2 s after the reconnect. Interactive `OnInitializedAsync` warns once if the accessor is
    empty (a framework change would silently bring the 401s back). The unused
    `PersistentAuthenticationStateProvider.cs` is deleted.
- **/map in hidden tabs (P1-E).** Browsers do not run `requestAnimationFrame` in hidden tabs; `initializeMap`
  awaited one (leaflet-interop.js fixed in the live-connection change above) and `initializePublicMap` did too,
  with a catch that resolved on error. Both create Leaflet synchronously now; a Leaflet error rejects as a
  JSException. `MapView.InitializeMapAsync`: one attempt; Debug when disposed / circuit gone / OnMapReady already
  arrived; Warning for a TaskCanceledException (call or reply lost with a dropped connection); one Error
  otherwise; logs the elapsed ms on the "call completed" Information line; on failure raises
  `OnMapInitialized(false)` → Map shows a persistent "The map failed to load." snackbar with a Reload action
  (`RequireInteraction`; a 5 s toast would expire unseen in a hidden tab — the dwell timer runs on the server).
  The duplicate unguarded `changeMap` in `MapView.OnAfterRenderAsync` (sent on every successful init) is gone.
  `circuit-pause.js` also arms the 5-minute pause for a page that LOADS hidden (no visibilitychange fires) —
  such tabs now fully initialize, so they must be reclaimable. The `JSInteropDefaultCallTimeout` comment no
  longer claims map init needs 2 minutes (the value stays: downloads use it).
- **No JS after the circuit is gone (P2-D).** .NET 10 runs the circuit handlers (→ `ReconnectionState` false)
  before it disposes components and marks JS permanently disconnected. `HandleCircuitDown` now only pauses both
  timers (its `disconnectSseUpdates` call was never delivered — +2 TaskCanceled two minutes later — and its
  snackbar/render flushed only after the reconnect, next to "Reconnected successfully"). `UpdateMarkers` and
  `CheckForMapPermission` return early while disconnected; `HandleCircuitUp` resumes both. Map, MapView,
  NotificationCenter and PublicMapView skip their JS teardown when `IsConnected` is false; every disposal step
  keeps a broad catch (Debug for expected interop failures, Warning otherwise); timers and
  DotNetObjectReferences are always disposed; `BrowserViewportService.UnsubscribeAsync` only if subscribed.
  `SafeJsInterop` (now takes `ReconnectionState`) skips calls while disconnected, never calls `console.*`
  through JS from its catch blocks, and logs a cancellation as Warning only while connected (a hang or a call
  lost across a reconnect), else Debug. Map's `OnInitializedAsync` returns early in static prerender (the
  `HttpContext != null` idiom): no more double fetch of config/markers/maps/custom markers/timers per load, and
  a `permissionResolved` flag shows "Loading map..." instead of flashing the "Waiting for Map Access" lock screen.
  A failed `OnInitializedAsync` (config/markers/maps call) now replaces the spinner with "The map failed to load"
  + a Reload button (`loadFailedMessage`) — it used to spin forever behind a 5 s toast.
- **Init sequence vs SPA navigation away.** `HandleMapInitialized` checks `disposed` after every phase, and
  `InitializeBrowserSseAsync` checks it again after its module import (a disposal during the import found no
  module to tear down; the stream would have opened bound to the dead page, holding its DotNetObjectReference,
  until the next /map visit). `MapView.OnMapReady` ignores a Leaflet `load` that arrives after disposal (its
  `dotnetRef` lives until the module-dispose round trip ends); `RefreshRoadsAsync` never re-imports the module
  after a disposal. PublicMapView's disposal logs expected interop failures at Debug like the others.
- **`toggleRoads` (P3-A).** `InvokeAsync<bool>` against a JS function returning `undefined` threw on every map
  load after all toggles were applied (regression from e03235b). Now `InvokeVoidAsync`, as are the 13 other
  ignored-result bool calls; `RoadManager.toggleRoads` returns an explicit boolean like its siblings.
- **Tests:** new project **`HnHMapperServer.Web.Tests`** (xUnit + bUnit 2.9.0 + Moq, references Web, in the
  solution so CI's root `dotnet test` runs it; `InternalsVisibleTo` from Web) — 50 tests:
  `AuthenticationDelegatingHandlerTests` (9, real factory pipeline: circuit identity without HttpContext; a
  timer created inside a circuit activity keeps auth after the request ends, with the HttpContext=null
  precondition asserted; never another user's cookie/IP on the same pooled handler; anonymous circuit;
  only HnH.Auth forwarded and caller headers replaced; no source → nothing + exactly one Warning even on 401;
  closed circuit / disposed scope send nothing and do not throw), `ServicesAccessorCircuitHandlerTests` (3:
  the accessor never leaks to the caller flow, close releases captured flows), `AuthenticationCaptureCircuitHandlerTests`
  (8), `MarkerPollBackoffTests` (7), `MapViewInitializationTests` (9, bUnit: changeMap exactly once when
  ready arrives mid-init, timeout = 1 Warning + `OnMapInitialized(false)`, JSException = 1 Error, disposed
  while pending = no changeMap / no failure, ready after disposal = no `OnMapInitialized`, JSDisconnected and
  lost changeMap reply after ready = Debug only, disconnected disposal skips JS), `SafeJsInteropTests` (8), `ReconnectionStateTests` (4),
  `MapNavigationServiceTests` (2, `SetMapRevision` returns false only for duplicates). Re-adding the old
  changeMap or dropping the circuit branch from the handler fails 6 of them. Service suite unchanged at 520.
- **Not unit-tested:** the JS changes and Map.razor.cs (≈20 injected services) — manual checks: `/map` opened
  with Ctrl+click and left hidden >2 min works on first look; `Blazor._internal.forceCloseConnection()` on /map
  → a markers call ~2 s after "connection restored", then every 60 s, all 200; stopping the API → 2/4/8 s
  retries, one "failing" snackbar, then "resumed" after restart without reload.
- **Ops:** the 18:05:06 / 18:06:26 drops of all circuits within 7 ms look like a Caddy reload; consider
  `stream_close_delay` on the websocket/SSE proxies (separate change, VPS Caddyfile).

### 2026-10-11: Live map connection — named SSE heartbeat, manual reconnect, polling fallback removed, JS-owned revisions

**From the 2026-10-10 export: the browser itself closed every `/map/updates` stream about every 17 s (270
streams, all 200, p50 17.0 s, 268 ending on the client watchdog's 8 s lattice), one tab sat in the polling
fallback for 13 minutes (a poll every 3 s), and 95.7% of all inbound JS→.NET calls (32,265 in 75 min) were
`OnSseMapRevision` — 97.6% of them duplicates.** Root cause: the API's keep-alive was an SSE *comment*
(`: keep-alive`), which EventSource never surfaces to JavaScript, while the watchdog (8 s tick / 12 s timeout)
counted only `onopen` and unnamed messages. Every quiet stream looked dead; each reconnect lost ~1 s of events
and replayed snapshot + tiles + every revision.
- **Heartbeat contract (server, `MapEndpoints.WatchGridUpdates`).** `Core/Constants/SseFrames.Heartbeat` =
  `event: heartbeat\ndata: {}\n\n`, sent once right before the live loop (inside the try: flushes the headers
  when the snapshot wrote nothing; not before the snapshot, which would commit the 200 before a snapshot failure)
  and then every 10 ticks (5 s) **unconditionally**, not only when idle. **It is a named event and never a
  comment, and it carries a data line** — neither a comment nor a data-less event is dispatched to JS.
  `Cache-Control: no-cache, no-transform` + `X-Accel-Buffering: no`. The notification stream keeps its comment
  keep-alive (notification-center.js has no watchdog).
- **Long-lived streams: subscribe first, bounded lifetime.** Two things the 17 s churn had been healing.
  (1) The hub subscriptions are now taken **before** the tile / revision / character snapshot reads; an event
  published in between used to be lost until the next reconnect, and on an hours-long stream that meant a ghost
  character (cleanup deletions are published once) or viewers stuck on an old `?v=` (served from the immutable
  browser cache). Landing in both is harmless: the snapshot replaces, revisions dedupe in JS, deltas upsert and the
  per-tick coalescing keeps their order; the channels are bounded (DropOldest). (2) Authorization (TenantMapAccess,
  Pointer, tenant) is evaluated only at connect and nothing bumps the security stamp, so a revoked Map/Pointer
  permission never reached an open /map once streams stayed up. The loop now ends the stream after
  `StreamMaxLifetime` (15 min, ±20% jitter so tabs opened together don't rotate together) by `break` — **never by
  cancelling `cts`**, which would land in the Error catch — sending any pending tile batch first. The client sees a
  healthy stream end and reconnects after ~1 s (backoff reset, `?since=` rebuilt) through a fresh authorization
  against the API principal (rebuilt from the DB every 10 s), and gets a characters snapshot + revision replay.
  Revocation therefore lands within ≤18 min. Non-replayed events (markers, pings, roads, timers…) published during
  that ~1 s gap are lost, as on any reconnect.
- **Client (`map-updates.js`, rewritten).** Every heartbeat, tile batch and named event refreshes liveness. The
  watchdog is armed at connect, ticks every 5 s and reconnects after 20 s without an event **plus the tick's own
  lateness** (hidden tabs clamp timers to ~1/min and frozen tabs run nothing; events queued meanwhile dispatch
  after the tick, so the stream is not blamed). **Reconnects are always manual** — any `onerror` (any readyState)
  or stale tick closes the stream — with exponential backoff 1 s → 30 s, ±20% jitter, retried forever, and the
  URL (`?since=`) rebuilt on every attempt (the browser's native retry would replay the stale one). A connection
  is **healthy** only after a heartbeat AND 20 s alive; only then do the backoff and the unhealthy streak reset
  (never at `onopen` — a stream dying in its snapshot phase or behind a buffering proxy still escalates). After
  3 unhealthy connections in a row JS calls `OnLiveUpdatesReconnecting(true)` once → Map shows a small persistent
  "Live updates reconnecting…" chip (where the polling chip was); `false` on the next healthy stream.
  `initializeSseUpdates` **always opens a fresh stream** (snapshot and revision replay are sent only at connect;
  it used to reuse an OPEN stream after a circuit reconnect/resume, leaving the rebuilt component without
  characters — the 17 s churn had been hiding that). `disconnectSseUpdates` stops everything it started (stream,
  watchdog, pending reconnect) and drops `dotnetRef`; `lastTileCache` is kept so re-entry asks for the delta.
  Events of a replaced stream are ignored (`es !== eventSource`).
- **Polling fallback removed (user decision).** It came in 38d5555 together with the watchdog that caused the
  drops, every one of the ~273 switches to polling in the export was triggered by that watchdog, it never went
  back to SSE, it re-sent all tenant revisions + a characters snapshot every 3 s, and polling-mode characters
  bound flat X/Y and rendered at 0,0. Deleted: `Api/Endpoints/PollingEndpoints.cs` + registration,
  `Core/DTOs/PollResponseDto.cs`, the Web `/map/api/v1/poll` dev proxy, the JS poll loop / `switchToPollingMode`
  / `retrySseConnection` and the initial HTTP fetch (`fetchInitialData` also used `/poll`; the first stream now
  sends the full tile list in 2k chunks, as it always could), `OnConnectionModeChanged` / `connectionMode` and
  the "Polling Mode" chip, and the dead, never-registered `Web/Services/MapUpdateService.cs`. Residual risk: a
  network that buffers streaming responses gets no live updates — now visible (the chip), not silently degraded.
  Tabs still running old JS get a 404 on `/poll` until the Web deploy's forced reload — harmless.
- **Revisions are JS-owned.** map-updates.js hands every `mapRevision` to `window.hnhMapper.setMapRevision`
  (registered by `initializeMap` next to `applyTileUpdates` — a bridge, not a second import: a second versioned
  instance of leaflet-interop.js would split the store). Exact duplicates are dropped; any other value is applied
  (an out-of-band import revision may be lower). .NET only seeds, in ONE call (`seedMapRevisions`: page init — was
  one awaited call per map, ~4.4 s for 94 maps — a newly created map, a map-list refetch), applied **only when the
  map is unknown or the value is greater**, so a GET snapshot never rolls back a live revision. `[JSInvokable]
  OnSseMapRevision` stays as a deduped fallback (`MapNavigationService.SetMapRevision` now returns `bool`) and no
  longer calls `StateHasChanged` (no markup shows a revision). The store is `SmartTileLayer.prototype.mapRevisions`
  — server-global like `cache`, deliberately kept across `initializeMap` re-entry.
- **Per-layer refresh scheduling (`smart-tile-layer.js`).** Pending-refresh state is per instance
  (`addInitHook`): the old prototype `revisionDebounce` was shared, so the overlay layer's call cleared the main
  layer's timer and the refresh never ran. When the timer fires: liveness re-check (`_map`, `mapId`, container
  connected); **no refresh while `document.hidden`** — every bump coalesces into one run on `visibilitychange`
  (it used to queue one rAF burst per bump); a newer run supersedes an older one's remaining batches; tiles whose
  preload failed are remembered (≤256) and merged into the next run (subsumed while every run covers the whole
  view; load-bearing once runs are narrowed to changed cells). Revision refreshes now build URLs with the overlay
  offset (`_serverCoords`) — required, since the overlay now really refreshes; nodes pruned mid-run are skipped
  instead of forcing a full `redraw()`. Still one full-viewport refresh per bump — changed-cell refresh is a later step.
- **Circuit pause / readiness.** `circuit-pause.js` calls `window.mapUpdates.suspend()` (= `disconnectSseUpdates`)
  after a successful pause — the stream otherwise kept an API connection open feeding a dead `dotnetRef`; the
  resumed Map re-initialises it. An early `charactersSnapshot` now waits (≤10 s) for circuit readiness instead of
  being dropped ("another snapshot will arrive soon" was only true because of the churn). `HandleCircuitUp`
  clears the chip before re-initialising (JS forgets it too; a still-failing stream raises it again).
- **Web dev proxy (`/map/updates`):** per-connection Warning/Error lines → Debug, `catch (OperationCanceledException)
  { return Results.Empty; }` before the generic catch, no 500 once streaming started. No Caddy change: a 502
  during an API restart is just a failed connect that the client retries with backoff.
- **Deploy order: API with or before Web.** New JS + old API sees no heartbeat: every idle map reconnects every
  ~20 s and shows the chip. Old JS + new API is harmless (it ignores `heartbeat` and keeps its old churn until
  reloaded). CI pushes the API image first; check it on a manual partial deploy.
- **Tests (520):** `SseFramesTests` (3 — a WHATWG event-stream parser: the heartbeat dispatches exactly one named
  event (so never `message`), `: keep-alive` dispatches nothing, a following unnamed tile batch still is
  `message`) and `SseWireContractTests` (3 — text tripwires on comment-stripped copies of `MapEndpoints.cs` /
  `map-updates.js`, see the csproj: the API sends `SseFrames.Heartbeat` and no comment keep-alive; every hub
  subscription in `WatchGridUpdates` precedes the three snapshot reads; the JS declares
  `HEARTBEAT_EVENT = 'heartbeat'` AND registers a listener on it — the name alone passed with the listener deleted —
  and never calls `/map/api/v1/poll`). The JS state machine has no harness — verified manually.

### 2026-10-11: Telemetry hygiene — secrets out of logs/traces, a log-level policy, no span per SignalR message

**From the 2026-10-10 production export (75 min): 22,678 API log records and 9,995 server spans carried a
full client token (`/client/{tenant}_{secret}`), 19,961 API Warnings were the `DIAGNOSTIC:` middleware
(path + player IP, twice per `/client` request), 97% of 122,744 Web records were Debug (~84k SignalR
"Received hub invocation" lines incl. circuit secrets), and 84,413 of 99,377 traces were one span per Blazor
hub message (p50 0.02 ms).** One change, three parts; policy and rules now live in Configuration →
"Logging levels and telemetry rules".
- **Secrets (P1-D).** `DIAGNOSTIC` middleware deleted; player names dropped (`SSE: First character` block,
  PositionUpdate lines); `DiscordWebhookService` no longer logs the signed preview URL (a 48 h credential).
  What the protocol makes unavoidable is masked by one list, `ServiceDefaults/Telemetry/SensitiveUrlRedactor`
  (`/client/{t}_***` keeps the tenant prefix for 401 attribution; invite validate/redeem, `/invite/{code}`,
  token revoke, `discord.com/api/webhooks/{id}/***`; a match ends at `/ ? #`, whitespace, quotes/backtick
  or `< > , ; ) ]`; `{...}` route templates are left alone, `/client//token` is still masked). Two adapters:
  `SensitiveUrlRedactionEnricher` — Serilog root enricher registered LAST in both `UseSerilog` lambdas through
  the shared `ApplyServiceDefaults()` (ServiceDefaults `Extensions`, also FromLogContext), so it
  also sees MEL scope properties (hosting `RequestPath`, IHttpClientFactory's `Scope` sequence + `Uri`) and
  covers Console, File and OTLP; walks string/Uri scalars and sequences, allocates nothing when clean — and
  `SensitiveUrlRedactionProcessor` (`OnEnd` masks `url.path`/`url.full`/legacy tags on every span, server and
  HttpClient incl. API→Discord; added before `UseOtlpExporter`'s processor, which goes last). ServiceDefaults
  gained `Serilog 4.3.0` (the version Serilog.AspNetCore 10 resolves).
- **Levels (P2-E).** Web `appsettings.json` lost its four Debug overrides (Map/MapView Debug moved to
  `appsettings.Development.json`; SignalR/Http.Connections Debug only via env var). Request logging uses
  Serilog's default `GetLevel` in both services (the custom one made every 4xx — tile 404s, scanner probes,
  stale-token 401s — a Warning). `OnRejected` logs one Warning per 429 (category
  `HnHMapperServer.Api.RateLimiting`, policy + route template); its old log was dead code (gated on
  `Items["TenantId"]`, which the limiter runs before). Routine lines → Debug: SSE lifecycle in
  `WatchGridUpdates` (a client disconnect is `catch (OperationCanceledException) when RequestAborted`; any
  other cancellation now reaches the Error branch), `/map/api/config` permissions line (now UserId, was
  username, was Information), PositionUpdate unknown GridID, MarkerUpdate per-marker lines, repaired marker
  payloads, every `TenantContextMiddleware` token rejection (the duplicate "Invalid token for tenant" line is
  gone; 401s stay countable per route in `http.server.request.duration`; P3-B later adds a rejection
  tracker), background-service per-tick heartbeats, GridService "Client in map" / existing-map response,
  `CharactersSnapshot`, per-retry marker-timer auth lines, failed-markers fetch (the handler and caller already
  log). Circuit "connection down"/"closed" → Information. `Console.WriteLine` → `ILogger` in NotificationCenter,
  EventsPanel, CreateTimerDialog (new `UiLog.FailureLevel`), MapView (except the init catch) and the Map ping
  handlers. EventsPanel: a 404 for a custom marker is Debug and the id is remembered per panel instance —
  `TimerHistory` has no FK to `CustomMarkers` (Timers do, SetNull), so a deleted marker's id stays in the
  history and was refetched on every panel open. `ENV ASPNETCORE_URLS` removed from `docker/api|web.Dockerfile` and the legacy root `Dockerfile`
  (it duplicated the base image's `ASPNETCORE_HTTP_PORTS` and warned at every start).
- **Traces (P2-F).** `EnableAspNetCoreSignalRSupport = false` in the `AddAspNetCoreInstrumentation` lambda
  (Filter only sees HTTP requests; our only hub is Blazor's ComponentHub). HTTP spans, Blazor Route/Event and
  Circuit spans stay at 100%. Accepted: ~144 lifecycle hub spans per 75 min are gone, and work started inside
  a hub call (JS-interop Web→API calls, timers created in `OnInitializedAsync`) now forms its own root traces
  instead of hanging off — and growing — a 15-minute page-load trace. A sampler was rejected: the runtime hands
  `Activity.Current` to it as parent, so ParentBased would also drop Blazor Event spans and JS-interop HTTP
  traces. **Re-enable the flag if a custom SignalR hub is ever added.**
- **Tests (514):** `SensitiveUrlRedactorTests` (49 cases incl. templates, delimiters, Discord, idempotence),
  `SensitiveUrlRedactionTests` (enricher: template props + rendered message, Uri props, HttpClient `Scope`
  sequence, hosting `RequestPath`, no realloc; span processor), `ServiceDefaultsTelemetryTests` (non-parallel
  collection — ActivityListeners are process-global: subscribed sources pinned, no SignalR; redaction before a
  later exporter; the real Serilog → OpenTelemetry logger pipeline with `IncludeScopes`; and the real
  `UseOtlpExporter` against a local OTLP/HTTP receiver — no secret in any exported byte. Three of them fail
  with the processor and flag reverted), `LoggingConfigurationTests` (base and base+Production Serilog levels
  ≥ Information for Api and Web; Web Development keeps Map Debug, not SignalR; both `Program.cs` still end their
  `UseSerilog` lambda with `ApplyServiceDefaults()`). The pipeline tests call `ApplyServiceDefaults()` itself, so
  they run the production enrichers. The test project now references ServiceDefaults +
  `Serilog.Extensions.Hosting` and copies both services' `appsettings*.json` and `Program.cs` (as text — it
  cannot reference Api/Web) to `config/`.
- **Ops after deploy:** purge the dashboard store (persistence mode Resume, volume `dashboard_data`), the old
  docker json logs and `/app/logs`, and the export zip; tokens are not rotated by default (operator-only
  exposure). Check the hand-copied compose sets no `ASPNETCORE_URLS` / `Serilog__MinimumLevel__*`.

### 2026-10-11: Map merges deleted imported zoom-0 PNGs their copied rows still pointed to (fixed)

**Data loss, found from production telemetry (silkcloth map 2813: the 5-minute WebP batch reported
`failed=471` on every scan, all cells with tile rows but no source files).** `.hmap` and public-map
imports write zoom-0 PNGs into the importing map's OWN directory (`tenants/{t}/{mapId}/0/{x}_{y}.png`);
client uploads use the grid-id pool (`tenants/{t}/grids/{gridId}.png`). `GridService.MergeMapsAsync`
copied the source's zoom-0 rows to the target with `File` unchanged and then (since 7090e37, 2026-08-22)
deleted `tenants/{t}/{src}/` recursively — the very files those rows named. The target could then never
build its pyramid for that area (dark at every zoom, silently counted as batch `failed`), re-importing
the .hmap does not help (existing grid ids are skipped), and only a re-walk healed a cell. Proven on the
dev copy too (meat-quail 2246 → 2242, 2026-09-03: 15 rows, all files gone). **The rows still hold the
exact original path + `FileSizeBytes`, so restoring the PNGs to those paths from a backup heals them.**
- **Re-home into the pool (PR-A):** each moved zoom-0 row whose File is a per-map zoom-0 path (any map
  id, either separator) is copied to `ITenantFilePathService.GetGridRelativePath(t, gridId)` before the
  directory goes — copy (temp file + no-overwrite rename), never a move, because a merge is not
  transactional. An existing pool PNG wins and is never overwritten; a source file that is already
  missing leaves the row UNCHANGED (restore key) and re-arms the grid's `NextUpdate` so the next client
  re-uploads it; a copy failure keeps the old path. **A contested source cell (several grids on one
  cell — corrupted DBs) is re-homed nowhere:** the per-map file is coord-keyed, so it cannot be
  attributed to any one of them, and copying it would put another grid's terrain under
  `grids/{id}.png`; the row moves unchanged and the directory guard keeps the file. One summary line
  per source map that had such rows (`Merge of map X into Y: N … re-homed …`; Warning when any row is
  missing/failed/contested). `GridService` gained the `ITenantFilePathService` constructor dependency
  (already registered in Api DI).
- **Directory guard:** `large/{src}` is always deleted; `tenants/{t}/{src}/` only when no LIVE-map row
  (every map of the tenant except the retired source; `File != ""`) references a file inside it — new
  `ITileService.AnyLiveTileReferencesDirectoryAsync`, the same rule as `MapIntegrityService`'s reference
  set, so merge, integrity scan and orphan purge agree. Otherwise the directory is kept with a
  `Kept directory of merged map …` warning. After the re-home this fires only for missing/failed/contested
  rows.
- **Tests (448):** `GridServiceMergeImportedTilesTests` (6, real SQLite: re-home + target WebP builds and
  shows the merged imagery; pool PNG wins untouched; dir kept for a live cross-map reference stored with
  backslashes; dir deleted when only dead-map rows reference it; missing source keeps the row + re-arms
  the grid; contested cell copies into no pool file and keeps its row). Existing merge/hardening
  fixtures only gained the constructor argument.
- **Detection shipped** (PR-B — see "Zero-length WebP files and missing tile sources" above) and the
  non-destructive repair (PR-C — Map Integrity → "Repair (non-destructive)", see the DirtyWebpCells
  entry above). Restoring the files from a backup is still the only way to bring imported imagery back.

### 2026-10-10: Tile generation — negative-cache race, failures saved as holes, empty-area recursion (fixed)

**Found in the first production telemetry export: 1,197 tile-generation errors in 75 minutes on
the Web, all thrown from `LargeTileService.EvictExpiredNegativeCacheEntries`** (1,024
`ArgumentNullException`, 173 `ArgumentException`), on zoom-1 tiles while serving zoom-5/6 requests.
Three layers, each fixed (commit 8f37328):
- **Empty-area recursion (why it fired constantly).** A zoomed-out tile over an unexplored area is
  never on disk, so every request regenerated it, and `GenerateZoomNLargeTileAsync` recursed through
  all descendants without checking for data: up to 4,096 zoom-0 queries and 5,461 negative-cache
  entries per zoom-6 tile. The 10,000-entry negative cache thrashed. **Fix:** one indexed `AnyAsync`
  over zoom-0 `Tiles` in the tile's footprint before recursing (uses `IX_Tiles_MapId_Zoom_CoordX_CoordY`;
  ≤2 ms worst case on the 73k-tile map). It also stops stale child files being composited where the
  DB has no data.
- **The race.** At the cap, each add evicted with LINQ `OrderBy` straight over the live
  `ConcurrentDictionary`. LINQ buffers via `ICollection<T>.Count` then `CopyTo` (two separate lock
  acquisitions): an add in between overflows the buffer (`ArgumentException`), a remove in between
  leaves default entries whose null keys sort first and reach `TryRemove(null)`
  (`ArgumentNullException`). **Fix:** `Services/NegativeTileCache` — eviction works on `ToArray()`
  (copied under all locks), runs single-flight, drops expired then soonest-to-expire down to 90%,
  value-matched removals. The positive tile cache snapshots too. **Rule: never run LINQ directly over
  a `ConcurrentDictionary` other threads mutate — `ToArray()` it first.**
- **Failures treated as "empty" (the damage).** `GenerateLargeTileAsync` caught every exception and
  returned null, so a failed tile was negative-cached, its parent was composed without that quarter
  and written to disk with a hole, and the endpoint answered a 404 that browsers cache.
  `ForceRegenerateLargeTileAsync` also negative-cached on failure, hiding the still-valid file.
  **Fix:** the recursion (`GetOrGenerateCoreAsync`) throws; public `GetOrGenerateLargeTileAsync` logs
  once and rethrows; the Web `/map/tiles` and API `ServeGridTileWebP` endpoints answer
  **503 + `Cache-Control: no-store`**; failed tiles are never negative-cached. Coalescing registers a
  `Lazy<Task>` before starting, so a request that loses the race no longer launches a duplicate.
- **Also:** `positionUpdate`, `gridUpdate` and `overlayUpload` answer 400 (not 500) to an empty or
  malformed body via `ClientEndpoints.ReadJsonBodyAsync<T>` (clients cut off mid-request around
  restarts post an empty body).
- **Tests:** `NegativeTileCacheTests` (TTL, removal, eviction order, concurrent stress) and four
  `LargeTileServiceTests` (child failure fails the parent with nothing saved; empty footprint doesn't
  negative-cache children; negative-coordinate footprints; failed regeneration doesn't hide the file).
- **Follow-up 2026-10-11 — pyramid-bounds guard.** Nothing checked zoom/x/y, and the footprint maths
  wraps outside the pyramid: `4 << zoom` masks the shift to 5 bits (zoom -4 spans a whole quadrant —
  up to 14,189 queries on the 73k-tile map; zoom 36 aliases zoom 4 past the negative cache), zooms 7+
  wrote real composites under `large/{map}/7..29/`, and z6 x ≥ 2^24 overflowed onto a real footprint
  (renders the data at x=0 and writes a duplicate pyramid). Any Map-permission member could send it.
  `LargeTileService.IsInPyramid` (zoom 0..`MaxZoom`=6, footprint inside int) guards both entry points:
  `GetOrGenerateLargeTileAsync` returns null (404, not 503), `ForceRegenerateLargeTileAsync` throws
  `ArgumentOutOfRangeException` instead of falling into its file-deleting "no sources" branch.
  Children of an in-pyramid tile are always in range, so only the entry points check.
  `PublicTileCacheService.EvictTo` sorted the live dictionary (same bug class as the 500) — it sorts a
  `ToArray()` snapshot now, with value-matched removals. `LargeTileServiceQueryCostTests` (real SQLite +
  command-counting interceptor) pin the cost model: empty z6 = 1 query then 0, z6 on disk = 0, sparse
  cold z6 = 25 (only the populated branch), out-of-range zoom/x/y = 0 queries and no files,
  ForceRegenerate out of range throws and deletes nothing. Deliberately not done: a cancellation token
  (generations are shared through the Lazy registry and run on the initiator's scoped DbContext),
  parallel child generation, warm-up — the numbers don't justify them.

### 2026-08-28: Cookbook panels/Favorites — catalog table design + sortable headers

**The panel/Favorites tables now render in the catalog tables' design and their headers sort
like the catalog's (user request).** The tables stay hand-rolled HTML (MudTable's RowTemplate
cannot carry the `draggable`/`@ondrop` attributes the reorder/move affordances need), aligned
instead:
- **Design:** `.panel-table` CSS mirrors the dense MudTable — body2 14px text on
  `--mud-palette-text-primary`, MudBlazor's dense cell padding (`6px 24px 6px 16px`,
  last-child 16px), `--mud-palette-table-lines` borders, `--mud-palette-table-hover` row hover,
  and the `#2c3e50`/600 header treatment app.css forces on every mud table header. Rows use the
  catalog's `food-icon` (32px) and weight-600 `food-name` instead of the old 18px chip styling.
- **Sorting:** headers reuse **MudBlazor's own sort-label classes** (`mud-table-sort-label
  mud-clickable` + `MudIcon ArrowUpward` with `mud-table-sort-label-icon mud-direction-asc/desc`)
  — MudBlazor.min.css's selectors are unprefixed, so they style themselves outside a mud-table
  and the arrow/hover/rotation are pixel-identical for free. Click cycles asc → desc → **off =
  the panel's own drag order** (same cycle as MudTableSortLabel); state is per panel
  (`_panelSort`), view-only, never persisted. Sortable: Food (name + variant label, the cell's
  own text), Energy, Hunger, Total, +2 Total, FEP/Hunger, +2 FEP/H; FEPs stays unsortable like
  the catalog's pill column. Raw values sort for the quality-scaled columns (the scale is one
  positive factor per column, so order is identical). A same-panel reorder **drop clears that
  panel's sort** — otherwise the drop would be invisible under the sort override.
- **FEP bars (same session, follow-up request):** each panel row's FEPs cell renders the
  detail view's `.fep-bar` under the pills — proportional segments by raw-value share of the
  row's Total, tier-2 diagonal hatch, "+2" label on segments ≥14% wide — sized like the
  variations table's bar (220px, 3px top margin) via a `.panel-table .fep-bar` override.
  Only rendered when Total > 0; unresolved entries keep their stored FEPs so they get one too.
- **Verified live** (dev DB, admin login): asc/desc/manual cycle exact on a 14-row panel,
  per-panel independence, computed header styles byte-equal to the catalog table's
  (`rgb(44,62,80)`/600/`6px 24px 6px 16px`); bars: segment widths sum to 100%, hatch pseudo
  renders, "+2" shown on wide tier-2 segments and suppressed under 14%; no new console errors.
  Suite untouched (UI-only; no bUnit harness then — Web-level tests now live in `HnHMapperServer.Web.Tests`). **Dev-cache footnote:** a rebuild that changes only a `.razor.css` does not
  touch the assembly, so `?v=` (assembly mtime) stays and browsers keep the old immutable-cached
  bundle — hard-refresh locally, or touch a .cs/.razor file; production's BUILD_COMMIT stamp is
  unaffected.

### 2026-08-26: Cookbook — panels as tables, multi-select AND filters, +2 made visible

Three user-decided changes on top of the value work below.
- **Panels and Favorites render as full-width tables, and the old chip strip is GONE**
  (kept for one iteration, then removed on request — it was redundant once the tables
  existed). Every panel is a collapsible `.panel-table-block` with the catalog's columns —
  FEPs, Energy, Hunger, Total, +2 Total, FEP/Hunger, +2 FEP/H — plus the remove ✕ on own
  panels. **No schema change:** `FoodPanels`/`FoodPanelItems` are untouched; the rows are
  built from the values the items already resolve at read time (`PanelRowsFor`), so a row
  needs the catalog only for its icon and the filter check. **Every affordance the strip
  carried moved onto the block** rather than being dropped: the block itself is the drop
  target (`droppable`/`drag-over`/`active` classes and their CSS were retargeted from
  `.panel-card`), rows are draggable and are drop targets for reorder/move
  (`StartChipDrag`/`HandleDropOnChipAsync`), and the header holds the collapse toggle,
  favourites star, name button (panel filter), share toggle and the rename/delete menu.
  ~130 lines of strip markup and 17 CSS rules (`.panel-card*`, `.panels-strip`,
  `.panel-item*`, `.item-line*`, `.item-bar*`, `.item-eff`, `.panel-count`) are deleted.
- **Filters apply to panel tables, and say what they hid** — each header reads
  `showing 4 of 12 — 8 hidden by filters`, so a curated list never silently loses entries.
  Visibility is decided at FOOD level (`PanelRowVisible`): a panel entry is a food you
  pinned, and hiding a pinned recipe because a sibling recipe matched reads as data loss.
  Unresolved entries always show — they are the "this food left the catalog" signal.
  The visible-name set is memoized on `FlatStateKey()` plus the async server-match count.
- **Every facet row is multi-select and AND-ed** (`_selectedStats/_selectedSatiations/
  _selectedPreps/_selectedWorlds`, was one nullable string each). AND **everywhere, no
  exceptions** per the user's decision: prep is one value per food, so two prep picks
  deliberately match nothing (the chip tooltip says so) rather than special-casing OR.
  - **Chip counts are now "add-one"**: each family's counts include its OWN selection, so a
    chip shows what the result becomes if you pick it next and a picked chip shows the
    current result count. The old exclude-own-family reading is meaningless once a family
    AND-s several picks. Same rule the ingredient wall already used.
  - **The FEP row gained +2 chips** (`STR` | `+2`): a token is `STR` (any tier) or `STR2`
    (the +2 line only, keyed exactly like `StatTierTotals`). Each picked chip adds its own
    sortable table column, so `VisibleColumnCount` is now `12 + _selectedStats.Count`.
    The chip renders when the catalog has such a line OR its live count is > 0 — the
    vocabulary is food-level, but in the Ungrouped view a +2 can exist only on recipes.
  - **World multi-select needed a value rule**: the facet also decides which per-world
    snapshot supplies the numbers. `EffectiveWorldGenus` = the NEWEST picked real world
    (Untagged-only → canonical values); filtering still AND-s every pick, so a row can be
    filtered by two worlds while showing one world's numbers. The active-filter chip for
    that world says which one is supplying values.
  - Quarter presets (≥25/50/75/100%) apply only with exactly one FEP chip picked
    (`SoleSelectedStat`) — with several there is no single stat a percentage could mean.
- **+2 FEPs are visible in the bars**: tier-2 segments in the detail `.fep-bar` and the panel
  cards' `.item-bar` carry a diagonal hatch (legible without colour — the two-tone hue alone
  was easy to miss) plus a "+2" label when the segment is ≥14% wide. The detail breakdown
  gained Total and, when they differ, a "+2 weighted" row; panel card lines show the weighted
  per-hunger only when the item actually has a +2.
- **Fixed a pre-existing test flake this surfaced:** twelve test classes called
  `SqliteConnection.ClearAllPools()` in Dispose. That is **global** — it disposes pooled
  connections belonging to OTHER test classes running in parallel, which then die with
  `ObjectDisposedException: SQLitePCL.sqlite3` mid-query. Adding two more SQLite classes
  tipped it over (1–2 random failures per run, in classes I had not touched, e.g.
  `AccountOverviewServiceTests`). Every call is gone and file-based test databases now use
  `;Pooling=False`, which also lets the temp file actually delete. 5 consecutive full runs
  green at 410. **Rule: never call `ClearAllPools()` in a test.**
- **Not unit-tested:** the facet logic lives in `Cookbook.razor`'s `@code` and there was no bUnit
  harness then (now: `HnHMapperServer.Web.Tests`); the Core halves (`wtotal`/`weff` parsing and evaluation) are covered.

### 2026-08-26: Cookbook values come from the game client, not the wiki (+ quality exponents)

**The reported symptom: /cookbook showed Seaberries hunger `0.1` while the game showed `0.016‰`.**
Root cause is not the tenant's data — it is precedence. `wiki-food-data.json` is **bundled in the
API image** and `GetBundledWikiAsync` is consulted on EVERY food creation, the admin dump import and
every client upload alike; `BuildFoodEntity` then took the wiki's hunger/energy/FEPs whenever the
page had them and used the game record only `if (!wikiMatched)`. So a food a player's own client
discovered still carried wiki numbers (prod row 4167 has `ContributedBy` set and wiki values), the
client's real numbers went only to `FoodVariants`, and nothing ever updated the food row afterwards —
ingestion never touched it after creation. **No tenant opted into wiki data; upload-only tenants were
affected exactly the same.** Measured on the dev prod-copy: of 931 foods, 568 held wiki values
verbatim and 553 of those (97%) disagreed with the tenant's own recorded observation.
- **Precedence is now client-wins** (`FoodValueSource`: Upload > Import > Wiki). The game record
  supplies Energy/Hunger/FEPs; the wiki keeps recipe text, cooking station, satiation groups,
  categories and page URL, and supplies values only for a food whose record carries none.
  `Foods.ValueSource` + `Foods.ValueWorld` record where the headline values came from (migration
  `AddFoodValueProvenance` — two ADD COLUMNs, applies in 10 ms on the 750 MB dev DB).
- **Uploads now update the food row.** `TryPromoteCanonical` runs per ingested record: higher source
  rank wins, then newer world, then (same source and world) the lowest FEP total — the
  closest-to-base heuristic variants already use. Without it a world's real numbers never reached
  the catalog.
- **`FoodVariantWorldValue.Observed`** distinguishes a real client observation from a snapshot the
  bulk world assignment seeded out of stored columns. **This was a live trap:** ingestion only
  replaced a world snapshot when the new FEP total was lower, so a seeded snapshot could reject real
  uploads forever. An observed record now replaces a seeded one unconditionally. The flag lives in
  the existing JSON column (no schema change); old rows read `false`, which is the correct reading.
- **The repair is one-time and automatic — there is no button and no endpoint** (user decision):
  `CookbookValueBackfillService` (Api hosted service, 30 s after start) calls
  `ICookbookValueBackfill.RunOnceAsync`, which walks every tenant through
  `FoodCatalogService.RefreshCanonicalValuesAsync` and then writes the marker Config row
  `cookbook.valueBackfillVersion = 1` under the `__global__` pseudo-tenant; later starts cost one
  query. A tenant that throws is logged and skipped **and the marker is withheld**, so the pass
  repeats next start rather than half-applying. Audited per changed tenant as
  `CookbookValuesRefreshed` with a null actor. **No re-upload, nothing deleted**: contributors,
  world tags, TimesSeen and ingredient signatures (so panels and favorites) are untouched. Bump
  `CurrentVersion` to force a re-run. Rehearsed against a copy of the dev DB:
  **632/931 foods updated in ~6 s**, Seaberries hunger
  0.1 → 0.02, Beech Nuts 0.2 → 0.01, Bark Bread 2.5 → 1.8 and its missing STR+1 1.75 line restored,
  Almonds unchanged, re-run updates 0.
- **Two guards, both found by that rehearsal:**
  - **Zero hunger is not data.** The client rounds hunger to 2 decimals (`round2Dig`), so a food
    under 0.005 arrives as `0` — Cave Slime is stored that way. Hunger then comes from the next
    observation that has one, else stays put (`HungerFallbacks` in the result).
  - **Information-free observations are skipped entirely.** Liquid names are volume-normalized
    (`"0.01 l of Cave Slime"` → `"Cave Slime"`), so a sip collapses onto the food with energy 4,
    hunger 0 and every FEP 0. The first rehearsal adopted it and wrote E=200 → 4 with all-zero FEPs;
    candidates with no FEP total AND no hunger are now dropped on both the refresh and the live path.
    **Known, not fixed:** all volumes of a drink share one variant row and lowest-total-wins keeps
    the smallest sip, so drink variants are systematically the smallest observed volume.
- **Quality exponents were wrong in the UI.** Hurricane normalizes uploads to q10 with
  `multiplier = sqrt(q/10)` for FEPs and `multiplier2 = sqrt(multiplier)` for hunger, so hunger
  scales with the **fourth root** of q/10 and FEP-per-hunger with the fourth root too — not the FEP
  factor. `/cookbook` printed hunger unscaled and scaled FEP/Hunger by `sqrt(q/10)`, overstating
  efficiency **2x at Q160** on the column that exists to rank efficiency. New `FoodQuality` /
  `FoodQualityScale` (Core) carry all three factors together so no caller can scale one without the
  others; `FepConditionEvaluator` takes the scale (hunger conditions now scale, energy still never
  does — it is quality-independent in the game), and the `eff>=` click-tool builds its threshold from
  the same scale so a clicked row still passes its own filter.
- **Client facts worth keeping** (Nightdawg/Hurricane@master, `automated/cookbook/FoodService.java`,
  `resutil/FoodInfo.java`): uploads carry raw `evs[i].a` and `glut` — **no** tooltip modifiers, so
  what we store is directly comparable to wiki base values; the tooltip's bigger numbers are
  `fepEfficiency` = satiation x hunger-bar `gmod` x account bonus (the "Food Efficiency: 359.99%"
  line = 1.2 x 3.0), which is why the screenshot's INT 0.36 = our stored 0.1 x 3.6 and needed no fix.
  Energy is never quality-normalized. Dedup is `MD5(itemName + resourceName + ingredients)` in a
  per-process cache, so values/quality do not affect it and every client restart re-sends. Truffled
  and peppered items are skipped, and nothing uploads while the hemp buff is up.
  **Not fixable server-side:** `round2Dig` (2 decimals) happens before the JSON is built, so the
  live 0.016 can only ever arrive as 0.02 — a client PR would be needed.
  **Open:** `FoodUploadRecordDto.Quality` (KamiClient-only) is never read; if that client does not
  pre-normalize, its uploads land at raw quality.
- **`+2 Total` column — +2 FEPs are worth double and the cookbook counted them once.** A +2 FEP
  raises the stat by 2 when it wins the bar roll, which is why the game prints both
  "(Actual) Total FEPs" and "(+2 Weighted) Total FEPs". Every total here was `Sum(f => f.Value)`,
  so Delicious Deer Dog (AGI+2 6, INT 2, PER 2) and Big Bear Banger (STR 6, STR+2 4) both showed
  **10** and sorted identically, where the game separates them **16** and **14**. New
  `WeightedTotalFep` / `WeightedTotal` on the row records (`Sum(value * tier)`), a sortable
  **+2 Total** column in the grouped table, flat view, variations sub-table and prep-compare, and a
  `wtotal` threshold key (`FepFilterKey.WeightedTotal`, parser + evaluator + chip + click-to-filter,
  same quality scaling as `total`). **Default ordering is now the weighted total** in the flat view
  and in prep-compare, since a +2-heavy recipe genuinely levels faster at equal raw total.
  **Caveat:** clients do not upload the game's own `sev`, and the resource script that computes it
  is not in the client source, so the column is OUR arithmetic (`value * tier`) — the standard
  community weighting, unverified against a live tooltip.
- **`+2 FEP/H` column — the actual leveling-efficiency number.** A +2 FEP fills the bar exactly
  like a +1 and wins the roll with the same odds (the winner is drawn by raw value share); only
  the payout doubles. Work the per-hunger algebra and the raw total cancels: one bar costs
  `R x hunger/total` and pays `wtotal/total` expected stat points, so
  **expected stat points per hunger = wtotal / hunger**. `FEP/Hunger` (raw) therefore measures
  bar fill per hunger, not leveling speed. Added alongside it rather than replacing it — a
  +1-only food reads identically in both, so the difference is self-explanatory where it matters
  — with a `weff` threshold key, click-to-filter, and the same fourth-root quality scaling as
  `eff`. `FEP/Hunger` keeps its meaning, so existing `eff>=` filters are untouched.
- **Tests (410):** `FoodCatalogCanonicalValueTests` (18 — repair pass precedence incl.
  observed-over-seeded, newest world, plain item over recipe variation, zero-hunger and
  information-free guards, wiki fallback, idempotence, tenant isolation, keyset paging past
  FoodBatchSize; live precedence incl. seeded-snapshot replacement, no downgrade from a newer world;
  import client-wins vs wiki-fallback; export/import provenance round-trip) and `FoodQualityTests`
  (7). `FepConditionEvaluatorTests` updated for the scale.

### 2026-08-25: Nested JS modules were never cache-busted (a deploy needed a hard refresh)

**Found while shipping the marker fix above: the fix did not take effect without Ctrl+F5.** Blazor
imports the ENTRY modules with a stamp (`./js/leaflet-interop.js?v={BuildInfo.Commit}` from
`MapView.razor`/`Map.razor.cs`, `?v={AssetVersion}` = assembly mtime ticks for the `<script>`/`<link>`
tags in `App.razor`), but a bare `import ... from './map/marker-manager.js'` inside a module does
**not** inherit that stamp — the browser fetches the sub-module under its own unversioned URL. Those
were served `Cache-Control: public, max-age=604800`, i.e. *fresh for a week with no revalidation*, so
a returning user ran a new `leaflet-interop.js` against nine week-old sub-modules. Every one of the
`wwwroot/js/map/*.js` managers was in that blind spot; the `?v=` on the entry module only ever
protected the entry module. Two changes, both verified:
- **`Web/Program.cs` static files:** `.js`/`.css` requested **with** a `?v=` are now
  `public, max-age=31536000, immutable` (that URL genuinely is immutable); **without** one they are
  `no-cache`, so the browser revalidates and ETag turns the common case into a ~200-byte 304.
- **`leaflet-interop.js` imports its sub-modules with its own stamp**, read from
  `new URL(import.meta.url).search` and appended via top-level-await `await import(...)` (the
  namespace objects behave exactly like `import * as X`, so no call site changed). **Only modules
  imported EXCLUSIVELY by leaflet-interop.js may be versioned this way** — a versioned URL is a
  separate module instance, so doing this to a shared module would instantiate it twice and split its
  state. `leaflet-config.js` (imported by ten files; its `HnHCRS` is compared by reference) stays a
  static unversioned import, as do `glow-icon.js` and `voronoi-adjacency.js`, which
  `map/marker-manager.js` and `public-map-interop.js` import directly. Those three rely on the
  `no-cache` revalidation instead.
- **`BUILD_COMMIT` is a Docker build ARG promoted to `ENV` in the runtime stage of
  `docker/web.Dockerfile`**, set from `git rev-parse --short HEAD` by CI, so `?v=` really does change
  on every deploy in production. It falls back to the literal `"dev"` when the env var is missing —
  which is why local JS edits still need a hard refresh (or `BUILD_COMMIT=<something> dotnet run`).
- **Verified** by running the app twice against the same warm-cache browser profile with
  `BUILD_COMMIT=aaa111` then `bbb222`: all eight sub-modules were re-requested as
  `…?v=bbb222` with status 200 and no hard refresh, the map rendered identically (316 icons), and the
  console was error-free. Suite 364/364.
- **Residual, self-healing:** a browser still holding a week-long cache entry for one of the three
  unversioned modules keeps it until it expires; from the first revalidation onward the `no-cache`
  header applies forever. Changing `leaflet-config.js`/`glow-icon.js`/`voronoi-adjacency.js` before
  then is the one case that can still be stale for a user, so prefer putting urgent JS fixes in a
  versioned module.

### 2026-08-25: Markers from the previous map stayed on screen after a map switch (fixed)

**Switching maps left the old map's markers rendered.** Measured against the dev DB: opening
"Main world" (map 150, 5 707 game markers + a custom marker) and switching to map 867 — which owns
exactly **3** cave markers and no custom markers — put **3 059** marker icons on screen; zooming out
re-clustered ~2 500 of the strays instead of dropping them. After the fix the same switch shows
exactly 3, and 0 once the detailed layer hides below zoom 6. Two independent causes:
- **`marker-manager.js removeMarker()` only did `mapInstance.removeLayer(marker)`.** Markers are
  added with `marker.addTo(markerLayer|detailedMarkerLayer)`, and **`markerLayer` is an
  `L.markerClusterGroup`** — the group keeps its own reference, so the marker is re-added on the next
  cluster refresh (any zoom) or when the group is re-added to the map. While clustered
  (zoom < `disableClusteringAtZoom: 6`) the marker isn't in `map._layers` at all, so the map-level
  removal is a plain no-op and the marker never leaves the cluster. `clearAllMarkers()` is built on
  `removeMarker`, so `changeMap()`'s clear did nothing for clustered markers. The correct detach
  already existed in `rebuildAllMarkers` (group + group + map); it is now the shared
  **`detachMarker()`** helper used by both, and `clearAllMarkers` additionally sweeps
  `markerLayer.clearLayers()` / `detailedMarkerLayer.clearLayers()` (also much cheaper on a cluster
  group: one rebuild instead of N). **Rule: never remove a Leaflet marker with `map.removeLayer()`
  alone when it was added to a LayerGroup/MarkerClusterGroup — remove it from the group.**
- **Custom markers were only ever cleared as a side effect of re-rendering them.**
  `CustomMarkerStateService.NeedsRendering` returned false when the freshly-loaded set was empty
  (marking it "rendered"), and `TryRenderPendingCustomMarkersAsync` repeated the same short-circuit —
  so `RenderCustomMarkersAsync`, the only caller of `ClearAllCustomMarkersAsync`, never ran when the
  **new** map had no custom markers, and the old map's stayed. Both short-circuits are gone: an empty
  set still gets a render pass, because that pass is what clears. JS `changeMap()` now also calls
  `CustomMarkerManager.clearAllCustomMarkers()` (before `flushCustomMarkerQueue`, which re-adds any
  queued markers for the new map) so the layer is correct regardless of the C# path taken.
- **Not affected:** characters (added straight to `mapInstance`), roads and pings (their clears use
  `clearLayers()`).
- **Verified live** (headless Chrome + CDP against the dev DB, A/B against the pre-fix build): game
  markers 3 059 → **3** on switch and 2 579 → **0** after zooming out; a custom marker placed at the
  spot the target map opens at showed as marker id 84 on the wrong map before the fix and is absent
  after; switching back restores all 3 056 markers of map 150, so nothing over-clears.
- **Related, NOT fixed here:** `SelectMarker` / `SelectCustomMarker` (sidebar list click that crosses
  maps) and `CenterOnCharacterAsync` hand-roll their own switch instead of calling `SwitchToMapAsync`,
  so they skip `ReloadMapScopedStateAsync` — characters, roads and the new map's custom markers are
  not re-loaded after those switches (things missing, not stale).

### 2026-08-25: .hmap markers stopped importing — the "mark" record is a tagged map now (fixed)

**Every marker in a current game-client export was silently dropped.** `HmapReader.ParseMarker`
read the leading version byte and `return null` for anything but **1**; today's client writes
**version 4** in a completely different layout, so `HmapData.Markers` came back empty. The import's
marker phase is gated on `Markers.Count > 0`, so it never ran and the result read **0 imported /
0 skipped with no error** — the failure was invisible from the UI. Reproduced on the user's
`test.hmap` (325 grids, 14 markers): 0 markers before, all 14 after. The two `.hmap` files in
`src/map/contributions/` are still version 1 (716 markers each), so the client changed the format
somewhere after 2026-01-17.
- **Version 4 layout** (reverse-engineered from the file, every one of the 14 records consumes its
  length exactly): `uint8 version=4`, `uint8 kind` (0x20 on every observed record), then
  `[T_STR key, tagged value]` pairs terminated by `T_END`, using Haven's `Message` tag ids —
  `nm` (T_STR name), `c` (**T_COORD, 2× int32, segment-relative TILE units**), `seg` (T_UID int64),
  `oid` (T_UID), `res` (**tag 0x22 = resource name string + uint16 version**), `color` (T_COLOR)
  for player markers. Grid records were NOT affected (still the version-4 struct; all 325 parse
  with zero trailing bytes), which is why terrain imported fine and only markers vanished.
- **Reader now dispatches on version**: 1 → the legacy fixed struct (unchanged), 2+ → the tagged
  map. **Marker class comes from the attributes, not the kind byte** (`res` → SMarker, otherwise
  PMarker), so an unseen kind still imports. The kind byte is undocumented, so the parse tries
  `with kind / without kind / tolerate-trailing` and keeps the framing that consumes the record
  exactly. Unknown value tags **throw rather than skip** — their payload length is unknown and
  skipping one would desync the record.
- **Silence is the real bug, so it is now reported**: `HmapData.MarkersUnreadable` +
  `MarkerParseIssues` count and explain undecodable records, and the import adds them to
  `MarkersSkipped` with a warning. A future format change surfaces as "N markers skipped" instead
  of a clean-looking zero.
- **Negative tile coords — grid lookup gained a floor fallback.** The marker's cell was
  `TileX / GRID_SIZE`, which truncates toward zero, so for negative coords it names the cell one
  short of the containing grid. **This is deliberately kept as the primary lookup**: the game
  client's own live marker uploads decompose exactly the same way (~45% of live `Markers` rows —
  8,319 of 18,420 across 7 tenants — carry a negative in-grid offset), `FrontendMarker` renders
  `grid.Coord * 100 + Position`, so the pair is self-consistent and lands on the right world tile,
  and matching the convention keeps the `{gridId}_{x}_{y}` Key identical so a re-import dedupes
  against live markers instead of doubling them. **Do not "normalize" these to 0–99.** The fix is
  only a fallback to the true containing cell (`FloorDiv`) when the export has no grid at the
  truncated one — those markers used to be dropped although their own grid was right there.
  Position is now derived as `tile - cell * GRID_SIZE` so both branches stay exact.
- **Also unblocks public maps**: `PublicMapGenerationService` filters hmap markers to thingwalls,
  so thingwalls from any post-change source had been missing there too.
- **Tests (364/364):** `HmapReaderMarkerTests` (6 — v4 SMarker with real bytes from `test.hmap`,
  v4 PMarker colour, kind-less framing, v1 `p`/`s` regression, undecodable record counted without
  losing its readable neighbour) and `HmapImportServiceMarkerTests` (3 — end-to-end v4 import
  asserting `cell * 100 + pos` is the original tile, the negative-coord floor fallback, and
  undecodable records reported as skipped).

### 2026-08-24: "Blazor Circuit Error: exception invoking 'OnSizeChanged'" after tab-restore (fixed)

**User-reported prod banner after returning to a long-hidden tab (loading overlay finishes → banner).
Root cause: circuit pause (`circuit-pause.js`, added 2026-08-23) orphans MudBlazor's JS-side
listeners.** Pausing evicts the server circuit, so component disposal cannot reach the browser
(MudBlazor swallows the `JSDisconnectedException`; its JS `cancelListener` never runs). The stale
`window.mudResizeObserver` entries keep watching the same DOM nodes with dead
`DotNetObjectReference`s; the reflow when the resume overlay clears fires them →
`invokeMethodAsync("OnSizeChanged")` against an object id the resumed circuit doesn't track. With
`DetailedErrors` off, EVERY server-side JSInvokable failure — including "no tracked object with id
N" — collapses into that same generic message; MudBlazor's JS never `.catch()`es the promise
(the `try/catch` in `resizeHandler` covers only sync throws), so it surfaced as an unhandled
rejection, which `blazor-diagnostics.js` painted as a scary circuit error. Two JS-only fixes:
- **`circuit-pause.js`**: after a pause actually succeeds, `purgeDeadDotNetListeners()` cancels
  every `window.mudResizeObserver._maps` entry (public `cancelListener(id)`) and calls
  `window.mudResizeListenerFactory.dispose()` (viewport `RaiseOnResized` — same class). Safe
  because a resumed circuit re-renders with fresh `firstRender` (the documented "comes back at
  defaults" behavior), so MudTabs re-`Observe`s and the viewport service re-registers. Purge is
  best-effort per registry; never purge when `pauseCircuit()` returned false (circuit still live).
- **`blazor-diagnostics.js`**: unhandled promise rejections are logged to console +
  `getBlazorDiagnostics()` but no longer show the "Blazor Circuit Error" banner — a rejected
  JS→.NET call doesn't break the circuit. The banner remains for circuit-level failures
  (start/reconnect hooks + Blazor's own fatal-error display).
- **Upstream (checked against MudBlazor `dev`, still unfixed there):** `cancelListener()` doesn't
  `clearTimeout` the pending ~200ms throttle and `resizeHandler`'s `invokeMethodAsync` has no
  `.catch()` — so a rarer non-pause variant (component disposed within the throttle window of a
  resize, e.g. navigating away from a MudTabs page mid-animation, when the JS cancel loses the RTT
  race) still rejects; it now lands in the console instead of the banner. Upgrading MudBlazor
  would not have fixed either. If other post-resume dead-ref errors ever appear (typing →
  KeyInterceptor, scrolling → ScrollListener), the same purge pattern applies to those registries.

### 2026-08-23: MUD0002 cleanup — 89 dead MudBlazor attributes ported or deleted (0 warnings)

**Every attribute MudBlazor was silently swallowing into `UserAttributes` is gone: 178 MUD0002
warnings (89 sites across 26 razor files — each is reported twice) → 0.** These predate the v8→v9
upgrade, so the configuration they expressed had never once taken effect. Removing them also cleared
12 `CS8974` and 4 `CS8601` warnings, which were the compiler noticing method groups being boxed into
that same dictionary. Suite 308/308; no new warnings; solution-wide MUD0002 is 0.
**The analyzer's `LowerCase` pattern is why `title=`/`aria-label=` pass and `Title=` does not** — an
attribute starting lowercase is assumed to be an HTML pass-through, anything else must be a real parameter.
- **Snackbar config moved to `AddMudServices`** (Web/Program.cs): `PositionClass` (bottom-left — the
  one real visual change; toasts had been rendering top-right all along), `MaxDisplayedSnackbars=5`
  and `PreventDuplicates=true` (both already the library defaults) and `HideTransitionDuration=400`
  (default 2000). **`VisibleStateDuration="400"` was deliberately NOT ported** — that is how long a
  toast stays readable (default 5000ms), not a transition timing; it was almost certainly meant as a
  twin of `HideTransitionDuration`, and honouring it now would make every toast in the app flash past
  unread. Verified live: container renders `mud-snackbar-location-bottom-left`, two identical adds
  collapse to one, eight distinct adds show five.
- **Two dead features came back to life.** `MudFab` has **no `ChildContent`** in v9, so the
  character-count `MudBadge` inside the map's floating menu button was being stringified into a bogus
  DOM attribute and never rendered — the badge now *wraps* the FAB (`MudBadge` carries
  `GetMenuButtonStyle()` so the fixed positioning still anchors it). `OnKeyPress` on `MudTextField`
  → **`OnKeyDown`** restored Enter-to-submit on Login/Register; those six fields also gained
  **`Immediate="true"`**, without which `keydown` fires before the `change` event commits the binding
  and Enter would post a stale (often empty) password.
- **Renames:** `Option`→`Value` on MudRadio and `@bind-SelectedOption`→`@bind-Value` on MudRadioGroup
  (CreateTimerDialog's timer-type selector was completely inert; note all three current call sites
  pass `ForceStandalone`/`Timer`/`PresetType`, so that radio group is not reachable from the UI
  today); `OpenMenuOnFocus`→`OpenOnFocus`; `DisableGutters`→`Gutters="false"`;
  `IsInitiallyExpanded`→`Expanded`; `OnClickStopPropagation="true"`→**`ClickPropagation="false"`**
  (inverted — five icon buttons nested in clickable rows were firing the row action too).
- **Deleted (no v9 equivalent, and nothing to preserve since they never worked):** `Clickable` on
  MudList (items carry their own `OnClick`), `Filter` + `@bind-SelectedChip` on MudChipSet (plus the
  never-read `_selectedPreset` field), `DisablePortal` on MudAutocomplete. **Note:** the
  `.icon-autocomplete-context .mud-popover` rule in `map-styles.css` has been dead since v7 — popovers
  always render through `MudPopoverProvider` at the end of `<body>`, never inside that wrapper. Left
  in place (out of scope); `PopoverClass="icon-autocomplete-popover"` is what actually styles it.
- **`Expanded` needs somewhere to live.** `MudExpansionPanels` lost `Expanded`/`ExpandedChanged` (it
  is per-panel now). Map.razor's controls hint just moved `@bind-Expanded` onto its panel, but
  EventsPanel's timer groups could not: `GetTimerGroups()` rebuilds its `TimerGroup` objects on every
  render, so binding to `group.IsExpanded` would snap the panel back to the hardcoded default on the
  next timer tick. Added `_groupPanelExpanded` (name-keyed dict) with `Expanded`+`ExpandedChanged`,
  so the group's own `IsExpanded` supplies only the initial value. **Rule: a MudBlazor `Expanded` fed
  from a per-render-rebuilt object needs its state held by the component, not the object.**
- **`Title` on 48 icon buttons + 1 FAB → `aria-label` + `title`.** `Title` was never inert here: Blazor
  splats `UserAttributes` verbatim and HTML lowercases attribute names, so it *was* rendering a working
  native tooltip. Dropping it would have been a visible regression, so both are set — `aria-label` for
  the robust accessible name, `title` for the unchanged hover tooltip. MapsPanel's eight overlay-nudge
  buttons got descriptive labels (`aria-label="X offset -10"`) while keeping the terse `title="-10"`;
  the icons there are left/right chevrons on both rows, so the labels say "X/Y offset", not up/down.
- **Verified in a real browser** (API+Web in a .NET 10 container, Chromium via Playwright): radio
  binding walks Standalone→Marker→CustomMarker; a chip click selects exactly one; an icon click
  increments only its own counter while the row's stays put; a collapsed group panel *stays* collapsed
  across a forced re-render; the FAB badge shows "7"; the controls hint expands and collapses with no
  gutter class. Zero console/page errors on dashboard, map, admin, superadmin and cookbook.
  **This app has no dark mode** — `MudThemeProvider` is used without `IsDarkMode`/`DarkTheme` and
  there is no `prefers-color-scheme` CSS, so light and dark render identically (confirmed by
  screenshotting under both); "Dark mode" is still the open Priority 3 item below.

### 2026-08-23: .NET 9/10 primitives — measured first, then adopted (or rejected)

**The question was "are we actually using what .NET 10 offers?" The answer split cleanly into
things that need no code at all and a short list of opt-in types.** Everything below was
benchmarked against this codebase's own shapes before any edit; the harnesses were throwaway
but the numbers are recorded here so nobody re-litigates them.

**Free — already active since the recompile, no code change.** Identical source compiled for
`net9.0` and `net10.0` and run back to back on this machine:

| pattern | .NET 9 | .NET 10 | |
|---|---|---|---|
| `OrderBy().Contains()` ×2,000 | 26.6 ms | 0.26 ms | **102×** |
| `Distinct().Contains()` ×2,000 | 14.4 ms | 0.25 ms | **58×** |
| `Where().Select().Contains()` ×20,000 | 100.1 ms | 6.3 ms | **16×** |
| `try/catch` (no throw) ×20M | 28.0 ms | 3.7 ms | **7.6×** |
| `Skip().Take().ToList()` ×20,000 | 10.4 ms | 1.7 ms | **6×** |
| `Dictionary<string,int>` lookups ×2M | 23.8 ms | 15.1 ms | 1.6× |

The codebase leans on these hard (139 `OrderBy`, 202 `Contains`, 58 `Distinct`, 116 `Any/All`,
608 `catch` blocks; 117 LINQ operators in `Cookbook.razor` alone). **Honest caveat:** the
escape-analysis items from the .NET 10 blog (capturing lambdas, small arrays,
`Stopwatch.StartNew`) showed **no change** here — allocation counts were byte-identical on both
runtimes. Those figures apply to narrower shapes than the general case; don't plan around them.

**Opt-in, measured and applied:**
- **`[GeneratedRegex]` everywhere — no interpreted regex remains in `src` outside tests.**
  Measured per 200k iterations: inline `Regex.Replace(text, "pattern")` **71.7 ms** (interpreted
  *and* a pattern-cache lookup per call) · `RegexOptions.Compiled` static **22.9 ms** ·
  source-generated **19.5 ms**. So inline sites gained ~3.6× and the `Compiled` statics a smaller
  step plus no IL emitted at first use (startup cost in both containers). Converted:
  `FepFilterParser` (2), `FoodCatalogService` (6), `CustomMarkerService`, `RoadService`,
  `TenantProvisioningService` (2), `PublicContributionEndpoints`, and the Razor ones via new
  `CookbookPatterns` / `Register.razor.cs` — **`[GeneratedRegex]` needs a partial method on a
  partial type, which a `.razor` `@code` block cannot declare**, hence the code-behind files.
  The three cookbook separator tidies were inline calls on the **per-keystroke** search path.
- **`FrozenDictionary`/`FrozenSet`** for the three build-once/read-many tables (`FepPalette`
  colours + full names, `FepFilterParser.StatKeys`): 16% on lookups (11.8 → 9.9 ms per 1M). A
  dense render does hundreds of these, not millions — this is microseconds, taken because it is
  free, not because it shows up.
- **`System.Threading.Lock`** for the four gates on request-serving paths (`LruImageCache`,
  `ZoomTileCache`, `AuthenticationStateCache`, public tile cache eviction): 28% uncontended
  (5.7 → 4.1 ns per acquire). Also stops those fields being used as arbitrary monitor objects.

**Opt-in, measured and REJECTED — the important one:**
- **`FrozenSet` for the cookbook's ingredient selection is 35% SLOWER** at realistic sizes.
  Full 49k-recipe scan with 5 picked ingredients: HashSet **4.7 ms** → FrozenSet **6.4 ms**. It
  only wins at ~60 picks (5.0 → 3.2 ms), which is not how the larder facet is used.
  `IngredientFilter` therefore keeps taking `IReadOnlySet<string>` and callers keep passing
  `HashSet` with `OrdinalIgnoreCase`. **Do not "optimize" this without re-measuring.**

**Fixed in passing:** `PublicMapService.GenerateSlug` and `CreatePublicMapDialog`'s live preview
each carried their own copy of the slug rule (the dialog's interpreted), so the preview could
silently stop matching the slug the server assigns. Both now call
`Core/PublicMaps/PublicMapSlug.Generate`; server semantics preserved (blank → `public-map`), the
dialog still shows nothing until you type. New `PublicMapSlugTests` pins it — it was untested and
it decides the URL of every public map. Also added a sanitizer test for uppercase/multiline
`<script>` blocks: those patterns' `IgnoreCase`/`Singleline` options moved into an attribute, and
losing one there would silently pass markup through XSS-facing code.

### 2026-08-23: Cookbook icon URLs — resource-name sanitizer (fixes the `f:` 404)

**/cookbook requested `/f:gfx/invobjs/leaf-brassica.png`**: a stored `Foods.ResourceName` carried an
`f:` prefix and `FoodIcons` built both the local URL and the havenandhearth.com fallback from it
verbatim, so that food's icon could never resolve from either source.
- **Not the wiki scraper.** `wiki-food-data.json` contains **zero** `gfx/` values, and its 59
  `File:`/`file:` strings all sit in `.metaobj.image` (wiki page filenames like `File:Milk.png`, read
  only through a generic key lookup). `ResourceName` is written from the client upload alone — the
  wiki enriches values, never resource names. The prefix comes from a **game-client upload**, and
  export→re-import preserved it verbatim, which is why it stuck.
- **New `FoodResourceName.Normalize`** (`Core/Cookbook/` — Core because the test project references
  Core but **not Web**, same reason `FepFilterParser`/`FepPalette` live there): strips `file:`/`f:`
  (case-insensitive, looped), strips leading slashes (`"/" + name` would otherwise become a
  protocol-relative `//host/...`), drops any char outside `[A-Za-z0-9/._-]`, and rejects `..` dot
  segments. Verified lossless against **all 2504** resource names shipped under `wwwroot/gfx` — every
  one matches `[a-z0-9/-]`.
- **Applied at both layers:** `FoodIcons.LocalSrc`/`RemoteFallback`/`RemoteFallbackOrHide` normalize
  when building URLs (repairs existing rows, no migration), and `FoodCatalogService` normalizes at
  every write path — `BuildFoodEntity` (the single choke point for legacy-dump *and* client-upload
  foods) and `ImportSnapshotAsync` — with both skip-guards now rejecting names that reduce to nothing
  (`"f:"`) instead of storing an empty resource.
- **Security note (why the charset filter, not just a prefix strip):** the three builders interpolate
  `ResourceName` into a **JS string literal inside an `onerror` attribute** at 5 render sites, and
  ingestion validated only *length*, never content — a quote in an uploaded resource name could break
  out. Dropping non-path characters closes that without a separate escaping step.
- **`leaf-brassica.png` genuinely does not ship** (no brassica icon among the 1991 in
  `wwwroot/gfx/invobjs/`), so for that food the remote fallback is the correct path; the fix is what
  makes that fallback URL well-formed rather than `.../mt/r/f:gfx/...`.
- **Tests:** `FoodResourceNameTests` (11 cases incl. the reported value, case/repeat/whitespace
  prefixes, leading slashes, dot segments, quote-escape, idempotence, shipped-name losslessness).

### 2026-08-23: Memory work on top of .NET 10 (branch `upgrade/net10`)

**Every number here was measured on the branch against a copy of the dev DB, driving the real
workload headlessly (login → /cookbook → 49k-row Ungrouped view → three facet filters), with
`DOTNET_PROCESSOR_COUNT=2` to look like the VPS.** Suite 308/308.
- **API: the bulk variation list is streamed, not buffered.** `GET /api/v1/cookbook/variations`
  built an entity list → a DTO list → an ordered copy → a JSON buffer: **123 MB → 541 MB private
  for one call**. It now returns `IAsyncEnumerable<FoodVariantDto>` from
  `FoodCatalogService.StreamAllVariationsAsync`, and the same call costs **+1.4 MB**. Output is
  **byte-identical** (36,223,344 bytes, verified by diffing captures from both implementations).
  Ordering survives because the query orders by `FoodId` in SQL and only one food's variations are
  buffered at a time — the secondary key (heaviest FEP total) is computed from JSON and cannot be
  sorted in SQLite. Contributor names come from a streamed pass over the `Contributors` column, so
  only the id set is held. `GetAllVariationsAsync` remains as a `ToList` over the stream (callers
  and tests unchanged). **Streaming trade-off:** headers go out before enumeration, so a mid-stream
  failure truncates the body instead of returning a Problem — the only consumer (the Web flat-row
  cache) treats that as a failed fetch and retries, same as it did for a 500.
- **Web runs Workstation GC** (`<ServerGarbageCollection>false</ServerGarbageCollection>`). The Web
  SDK silently defaults ASP.NET Core to Server GC (a heap per core). Measured: **467 MB WS / 358 MB
  private → 390/266**. Cost: the one-off 49k-row build 2.3s → 2.9s; filters unchanged. This also
  turns DATAS off (it is Server-GC-only). **The API keeps Server GC** — parallel image work.
  Full matrix that was run: Server 16-cpu 443/357 · Server 2-cpu 467/358 · **Workstation 2-cpu
  390/266** · Workstation+`GCConserveMemory=5` 392/268 (no further gain) · Server + 500 MB heap hard
  limit 441/326.
- **`PublicTileCacheService` is bounded.** It held *every* public-map tile PNG in a
  `ConcurrentDictionary` forever — the ~1.2 GB baseline noted in docker-compose, growing with the
  public-map library. Now a byte budget (**`PublicTileCache:BudgetMB`, default 256**) with LRU
  eviction; preload stops at the budget and logs what it left on disk. Safe because the tile
  endpoint already fell back to disk and re-cached on a miss, and tiles are served `immutable` with
  year-long cache headers. Verified with a 1 MB budget against 5 MB of tiles: 24 resident / 96 on
  disk, 28/28 fetched tiles byte-identical to disk, 120 requests of eviction churn, zero errors.
- **`CookbookFlatCache` caps built rows at the 2 most recently used worlds** (`MaxWorldsPerTenant`).
  It kept a full ~49k-row set *per world* per tenant for every world anyone clicked; rebuilding from
  the cached DTOs takes ~2s.
- **Circuit configuration:**
  - **`MaximumParallelInvocationsPerClient = 10` REMOVED.** Blazor requires the default of 1;
    raising it breaks `IBrowserFile` uploads (dotnet/aspnetcore#53951) — which is exactly what the
    .hmap and cookbook imports use. This was a live bug, not a tuning choice.
  - `DetailedErrors` back to development-only (the TODO left in the code).
  - `DisconnectedCircuitMaxRetained` 100 → **20**.
  - **.NET 10 silently enables circuit state persistence** with `AddInteractiveServerComponents()` —
    default **1000 persisted circuits kept 2 hours**, which nothing here opted into. Bounded to
    50 / 30 min.
  - **`wwwroot/js/circuit-pause.js`** pauses a circuit after its tab is hidden **5 minutes**
    (`Blazor.pauseCircuit()`, new in .NET 10) and resumes on return. **The reload fallback is
    load-bearing**: if `resumeCircuit()` returns false the persisted state is gone and the page is
    silently dead (dotnet/aspnetcore#64607, fixed only in 11.0-preview1) — we reload instead.
    Verified live: both calls return `true`, page stays interactive. Five minutes rather than the
    docs' instant pause because **no page annotates `[PersistentState]` yet**, so a resumed page
    comes back at its defaults (cookbook filters reset). Annotating that state is the follow-up that
    would let the delay drop.
- **ImageSharp allocator capped** in both services (`MaximumPoolSizeMegabytes = 32`,
  `AllocationLimitMegabytes = 256`). *(Corrected 2026-10-11:)* the cap covers only ImageSharp's
  unmanaged pool, which serves buffers larger than 1 MiB. Everything up to 1 MiB — including each
  400x400 tile frame (640,000 B, a pooled 1 MiB LOH array) — comes from `ArrayPool<byte>.Shared` on the
  managed heap and is uncapped; `AllocationLimitMegabytes` is a per-allocation ceiling and
  `MaxDegreeOfParallelism` is per operation. Tile serving re-verified at z0/z1/z2/z4/z6 (200, 13–16 ms).
- **Web→API requests gzip** (`AutomaticDecompression` on the `APIUpload` client): the bulk body is
  **36 MB raw / 3.4 MB gzipped**; the API already had response compression with SSE excluded.
- **Observability:** ServiceDefaults now subscribes to the .NET 10 Blazor meters
  (`aspnetcore.components.circuit.active` / `.connected` / `.duration`, lifecycle, render diff),
  `Microsoft.AspNetCore.MemoryPool`, and the Identity/Authorization meters, plus the circuit and
  component ActivitySources. **Blazor's instruments are inert until something subscribes**, so this
  is the opt-in. `active` minus `connected` is the retained-circuit backlog — the number that
  predicts Blazor Server memory. Note the circuit instruments carry **no tags**, so they cannot be
  split by tenant, and `render_diff.size` buckets top out at 100 elements (the 49k-row table will
  saturate the top bucket; add an `AddView` if that signal is wanted).
- **Deliberately NOT done, with reasons:** HybridCache for the flat cache (it serializes on write
  even L1-only, so it would JSON-encode ~100 MB per refresh — strictly worse than the existing
  `Lazy<Task<T>>`); `PublishReadyToRun` (documented to *increase* working set); Native AOT
  (Blazor Server is unsupported); `InvariantGlobalization` (breaks lv-LV formatting);
  `AddDbContextPool` (the tenant filter reads `IHttpContextAccessor`, and pooling would capture the
  first request's accessor — a cross-tenant leak, not just a perf question); global `NoTracking`
  (would break every read-modify-save path; the big reads already use `AsNoTracking`).
- **Net effect for one user opening the 49k-row view:** Web **467/358 → 416/289 MB**; API peak for
  the bulk endpoint **−400 MB**; public tile cache gains a ceiling it never had.
- **Note:** `docker-compose.yml` already caps both services at `mem_limit: 3g`, so the GC does get a
  container limit to size against (75% of it). No `cpus` limit is set, which means
  `Environment.ProcessorCount` — and therefore the API's Server GC heap count — follows the host.
  Capping CPU is the *supported* way to bound heaps; `GCHeapCount` must not be used for it, because
  on .NET 10 setting it disables DATAS.

### 2026-08-23: .NET 10 (LTS) + MudBlazor 9 upgrade (branch `upgrade/net10`)

**.NET 9 and .NET 8 both lose support on 2026-11-10; .NET 10 is supported until 2028-11-14.**
Two commits: framework/packages first (MudBlazor untouched), then MudBlazor 9 — so a UI
regression is attributable to one of them. Clean build 0 errors, 306/306 tests, both services
verified against a copy of the dev DB (EF Core 10 reports the schema up to date; tiles serve at
z0/z2/z5; Blazor circuit, chip filters, header sort, dialogs, tabs, uploads all exercised live).
- **Frameworks/packages:** all 8 projects → `net10.0` (ServiceDefaults was still `net8.0`);
  `global.json` pins the 10.0.x SDK (`rollForward: latestFeature`); EF Core / Identity /
  DataProtection 9.0.10 → **10.0.11**; **Aspire 9.5.1 → 13.5.2** (9.5.x is out of support — the
  AppHost drops the `Aspire.Hosting.AppHost` PackageReference, which the SDK now supplies, and sets
  `<NoWarn>ASPIRE010</NoWarn>` because orchestration deps come from NuGet, not the Aspire CLI
  bundle); Http.Resilience 8.10.0 → 10.9.0, ServiceDiscovery 8.2.2 → 10.9.0, OpenTelemetry
  1.10.0/1.15.3 → 1.18.0; Serilog.AspNetCore 10.0.0; Steam OpenID / Discord OAuth 10.0.0;
  Test.Sdk 18.9.0, coverlet 10.0.1, xunit 2.9.3 (last v2 — still VSTest; MTP stays opt-in).
- **Code changes the upgrade forced (only two):** `ForwardedHeadersOptions.KnownNetworks` →
  `KnownIPNetworks` (obsolete, ASPDEPR005) in both `Program.cs`; `Password.razor`'s
  `[SupplyParameterFromForm]` property lost its initializer (new **BL0008** analyzer: a field-less
  post overwrites it with null) and is initialized in `OnInitialized` instead.
- **Package hygiene:** BCrypt.Net-Next removed (no call sites since the Identity migration);
  deprecated `Microsoft.AspNetCore.Http.Abstractions` 2.2.0 / `Microsoft.AspNetCore.Http` 2.2.2
  replaced by `<FrameworkReference Include="Microsoft.AspNetCore.App" />`;
  `Microsoft.AspNetCore.DataProtection` dropped from Tests (framework-provided, NU1510);
  **AngleSharp pinned to 1.7.1** in Web — Steam OpenID pulls ≥ 1.3.0, which carries
  CVE-2026-54570 (mXSS); 1.5.0+ is patched.
- **Deployment:** `docker/*.Dockerfile` and the stale root `Dockerfile` → `sdk:10.0`/`aspnet:10.0`,
  CI `setup-dotnet` → `10.0.x`. **The 10.0 tags are Ubuntu 24.04, not Debian** (Debian images are
  not published for .NET 10) — watch the first deploy for native-dependency surprises
  (`e_sqlite3`, ICU, fonts).
- **MudBlazor 8.13.0 → 9.8.0** (8.x ended at 8.15.0 and never shipped a `net10.0` build):
  `IDialogService.ShowMessageBox` → **`ShowMessageBoxAsync`** (27 sites/18 files);
  `MudDataGrid.ServerData` gains a `CancellationToken` (MapManagement); **silent** ones —
  `MudTabs.PanelClass` → **`TabPanelsClass`** (5 sites; the old name silently stops applying panel
  padding) and `MudFileUpload.ActivatorContent` → **`CustomContent`**, whose context is the upload
  component so the trigger must call `OpenFilePickerAsync()` itself (cookbook import, hmap sources,
  hmap map import — otherwise the button renders nothing); `MudForm.Validate()` → `ValidateAsync()`.
  **Popovers are non-modal by default now** (the `Modal="false"` workarounds are redundant but
  harmless) and overflow is `FlipAlways`. Wins: MudTable no longer resets `RowsPerPage`/one-way
  `CurrentPage` on parent re-render (the flat cookbook view re-renders on every filter change),
  no needless MudTooltip re-renders, modeless overlays reopen on the same activator,
  MudAutocomplete disposes its previous search CTS, every internal regex has a match timeout.
- **Fixed in passing:** `<SignInMethodIcons>` was unresolved in `Admin/UserManagement.razor` and
  `Pages/TenantDetails.razor` (only `AccountsOverview.razor` imported `Components.Shared`), so the
  Sign-in column rendered a dead HTML tag on those two pages — `Components.Shared` is now in
  `Components/_Imports.razor`.
- **Deliberately NOT taken: SixLabors.ImageSharp 4.x.** 4.0 added **build-time license
  enforcement** — a direct dependency needs a `sixlabors.lic` file or `$(SixLaborsLicenseKey)`, and
  4.1.1 emits "No Six Labors license found" warnings on every build. The licence terms themselves
  are unchanged (free for open-source / non-profit / under-$1M revenue) but a key must be applied
  for at licensing.sixlabors.com and wired into local builds AND CI. Staying on **3.1.12** (no
  advisories, no enforcement). Revisit only if the 4.x perf work (resize convolution, Vector128
  JPEG paths, lower WebP-lossless allocations, configurable `MemoryAllocator` limits) is worth the
  licence chore; the only code change needed was two test helpers (`Color.FromPixel(...)`).
- **Known follow-ups — both now done (2026-08-23):** the ~89 dead MudBlazor attributes flagged by
  MUD0002 since the v8 days (see the MUD0002 cleanup entry above), and the malformed icon URL
  `/f:gfx/invobjs/leaf-brassica.png` (an `f:`-prefixed path leaking into `FoodIcons`; see the
  cookbook icon-URL sanitizer entry above).
  Optional .NET 10 adoption work: named query filters for the 17 tenant filters,
  `TypedResults.ServerSentEvents` for the 10 hand-rolled SSE writers, Blazor circuit
  pause/resume + `[PersistentState]` (the disconnected-circuit memory problem behind
  `CookbookFlatCache`), Blazor circuit metrics (`circuit.active` / `circuit.connected`) in Grafana,
  complex-type JSON columns with `ExecuteUpdate` support, C# 14 `field` in the parse-on-set
  cookbook property setters.

### 2026-08-23: Self-service onboarding — create/join maps, auto-join invite links, Steam + Discord sign-in

**Players now onboard themselves: any signed-in player can create a tenant ("map" in all player-facing
copy) and becomes its admin, and admins share multi-use invite links that join immediately — the link IS
the approval. Research confirmed Haven & Hearth has no identity provider of its own (its own website signs
players in via Steam OpenID), so "Sign in through Steam" plus Discord OAuth were added as optional
providers — switched on, configured and explained in SuperAdmin → Sign-in (no deployment changes).**
Suite 303/303.
- **Session foundation (prerequisite — tenant switching was broken end-to-end):** both claims factories
  picked the first-ever membership and `POST /api/auth/select-tenant` discarded the claims it built (wrong
  `SignInAsync` overload; its Set-Cookie could never reach the browser through the `UseCookies=false`
  Web→API client). Now: `AspNetUsers.ActiveTenantId` (+ `RegistrationSource`, `LastLoginAt`; migration
  `AddActiveTenantAndAccountOrigin`), one canonical `Infrastructure/Identity/TenantClaimsPrincipalFactory`
  on `ActiveTenantMembershipResolver` (Web + Api copies deleted), Web-side `/api/tenant/select` switch
  endpoint (`TenantSwitchUrl.For` helper; every UI switch is a `forceLoad` navigation), `/api/login` signs
  in tenant-less users and lands them on `/tenant/select` (the old `no_tenant` refusal is gone), API
  `select-tenant` is persist-only. **No stamp bump on switch** (it would sign the user out everywhere):
  Web `OnValidatePrincipal` also rebuilds on active-tenant drift (and compares Identity roles excluding the
  tenant Role claim), `RevalidatingIdentityAuthenticationStateProvider` invalidates circuits whose TenantId
  claim ≠ resolved active tenant. Dashboard/Map/Cookbook redirect tenant-less users to `/tenant/select`.
  `AssignUserToTenant` now refuses only duplicates in the SAME tenant (multi-membership works).
- **`ITenantMembershipService`** (`Services/Services/TenantMembershipService.cs`) is the single writer of
  `TenantUsers` rows: `AddMemberAsync(AddMemberRequest)` (Joined / ApprovedExisting — legacy pending rows
  flipped in place with permission merge + invitation flag cleared / AlreadyMember / TenantNotFound /
  TenantInactive; audits with the Identity user id as actor; records `TenantUsers.JoinSource` +
  `InvitationId` — migration `AddInvitationMultiUseAndJoinSource`, existing rows read "Legacy") and
  `RedeemInvitationAsync` (validate → already-member shortcut consumes nothing → transaction { atomic
  `TryClaimUseAsync` → AddMember(role **always** TenantUser, permissions = the link's stored preset,
  active tenant set) }). Registered in Api AND Web (the external-sign-in callback redeems in-process —
  no HnH.Auth cookie exists yet). `ApproveUser` (legacy) delegates to it; bootstrap/assign tag their source.
- **Invite links are multi-use:** `TenantInvitations.MaxUses` (null = unlimited; pre-existing rows
  back-filled to 1, redeemed ones to UseCount 1), `UseCount`, `Permissions` (JSON list = preset "Full" —
  all five incl. Writer, the default per the user's decision — or "Contribute" = no Writer); `UsedBy`/`UsedAt`
  = last redeemer. `TryClaimUseAsync` = one conditional `ExecuteUpdate` (active ∧ unexpired ∧ uses left)
  so two redeemers racing the last use get exactly one winner; `Status="Used"` only when exhausted.
  Expiry choices 7/30/90 days (server-clamped), uses 1–100 or unlimited. Short link form
  `{base}/invite/{code}` everywhere (`InviteLinks.Build/TryExtractCode`).
- **Invitation endpoints locked down:** the unauthenticated full-record `GET /api/invitations/{code}` is
  DELETED (it leaked TenantId/UsedBy); public `GET /validate/{code}` returns only name / inviter / member
  count / expiry / preset for valid codes and a generic "invalid or expired" otherwise; new
  `POST /api/invitations/{code}/redeem`; the `/api/tenants/{tenantId}/invitations` group now requires the
  TenantAdmin policy AND the route-vs-claim tenant check (list could previously enumerate any tenant's
  codes; revoke ignored the tenant — `RevokeInvitationAsync(id, tenantId)` is ownership-checked). Audits
  `InvitationCreated/Revoked/Redeemed`. No EF global filter on TenantInvitations (validate/redeem run
  without tenant context) — explicit predicates + endpoint auth are the mechanism.
- **Register** (`POST /api/auth/register`): with a link → account + immediate join (`joined:true`), the
  Register page then auto-signs-in via the Web cookie form; without a link → gated by
  `SelfRegistration:Enabled` **in the API** (the flag previously only hid a button and was set on the wrong
  container) — a valid link always permits registration. Sets `RegistrationSource=Password`, audit
  `UserRegistered`. New IP rate-limit policies `Register` 5/h, `Login` 20/min, `TenantCreate` 3/h,
  `InviteRedeem` 10/min, `InviteValidate` 30/min. **Client IP through the Web hop:**
  `AuthenticationDelegatingHandler` forwards `X-Forwarded-For`/`-Proto` — from the circuit's own
  `AuthenticationStateCache.ClientOrigin` (captured per circuit by `AuthenticationCaptureCircuitHandler`, read
  through `CircuitServicesAccessor`; since 2026-10-11) for circuit work, from the current HttpContext for HTTP
  requests — without it every web user shared the web container's bucket and audit IP.
- **Self-service tenants:** `ITenantProvisioningService.CreateOwnedTenantAsync(userId, name?)` — settings
  (SuperAdmin → Sign-in; deployment config only seeds): enabled (kill switch), quota (1024 MB; never
  client-supplied), owned-tenant cap (3, counts TenantAdmin memberships in active tenants), and
  **`SelfServiceTenantsRequireExternalIdentity` (default ON, user decision 2026-08-23): password-only accounts
  cannot create maps** — they join with an invite link or a superadmin creates the tenant and assigns them
  (SuperAdmin → Unassigned Users, the pre-existing flow); an account with any `AspNetUserLogins` row
  (Steam/Discord sign-in or linked later) or the SuperAdmin role is eligible. `GetOptionsAsync(userId)` →
  `{enabled, eligible, reason}` drives the "Create a new map" card (ineligible state explains + links to
  `/account`); `CreateOwnedTenantAsync` returns `NotEligible` (403) regardless of UI. One transaction: tenant
  (generated `icon-icon-NNNN` id, optional display name 3–40 chars applied via `UpdateTenantAsync`) +
  owner membership (TenantAdmin, all five permissions, `JoinSource=SelfCreated`, active tenant) + audit
  `TenantSelfCreated`; directories pre-created best-effort. `POST /api/tenants/self {name?}` (201 / 400 name
  / 403 disabled / 409 cap), `GET /api/tenants/self/options`.
- **UI (the fork must be unmissable):** `/tenant/select` (`TenantSelector.razor`) is the hub — zero maps →
  welcome screen with exactly two big cards `Shared/CreateMapCard.razor` / `Shared/JoinMapCard.razor`
  (paste a link or code → live preview "Invite to X · N members" → *Join X*); with maps → "Your maps" grid
  (current one checked) + the same two cards (`?action=create|join` highlights one). Dashboard/Map/Cookbook
  funnel tenant-less users there. `TenantSelectorDropdown` (top bar) is ALWAYS visible: current map name,
  switch, *Create a new map*, *Join a map with an invite*, *Copy invite link* (admins; reuses the newest
  live link or creates a 7-day unlimited one). Dashboard gained a "Your map" card and an admin "Invite
  players" card with the link ready to copy. `/invite/{code}` (`InviteRedirect.razor`) is a real landing
  page: "You're invited to join X · invited by Y · N members · valid D more days" → signed-in: *Join X*;
  anonymous: Continue with Steam/Discord (when enabled) / Create an account / Log in (round-trips via
  `returnUrl`). Admin → Invitations: expiry + uses + access-preset selects (`Modal="false"`), Uses/Access
  columns, "Used up" tab. New `ClipboardService` replaces three copy-paste helpers. Player-facing wording is
  "map" (never tenant/organization) on all touched surfaces ("Map admin" nav button). **Readability rules
  learned here:** SuperAdmin tabs must wrap content in a plain `MudPaper` (the `.superadmin-panel .mud-paper`
  rule supplies the 80% glass; a bare `MudTable` with `glass-morphism` is see-through), and the hub page's
  panels use the opaque `.onboarding-card` (white, no backdrop-filter) — `glass-morphism` on pages without a
  glass backing is too transparent for text-heavy cards. Login page = three buttons (Steam / Discord /
  Username and password); the password form (with the "create an account" link) appears only after choosing
  it, or directly when it is the only method, after a failed password login, or after registering.
- **Sign-in settings are superadmin-managed at runtime (user request):** `IAuthSettingsStore` /
  `AuthSettingsStore` (Services) keeps `AuthSettings` (open registration, self-service maps + quota + cap,
  Steam on/off + Web API key, Discord on/off + client id/secret) as global `Config` rows (`auth.*`,
  tenant `__global__` — created on demand), **secrets encrypted** with `IDataProtectionProvider`
  (purpose `HnHMapperServer.AuthSettings.v1`, shared key ring, `dp1:` prefix; undecryptable → treated as
  unset + warning). **Authorization is enforced in the store itself**: `GetViewAsync`/`SaveAsync` take the
  caller's `ClaimsPrincipal` and require SuperAdmin by claim AND by database role (`UnauthorizedAccessException`
  otherwise; the audit actor comes from the principal). Everyone else gets `GetPolicyAsync()` → `AuthPolicy`
  (flags only, no string fields). Secrets are decrypted only where `AuthSettingsStoreOptions.DecryptSecrets`
  is on (Web); the API process never holds plaintext (presence flags `SteamKeyConfigured` /
  `DiscordSecretConfigured` keep both processes agreeing on what is active). No HTTP endpoint exposes the
  settings — the page writes in-process over the circuit. Process singleton `AuthSettingsCache` (15 s TTL,
  `Changed` event on save); deployment config (`SelfRegistration:*`, `TenantSelfService:*`,
  `Authentication:*`) only seeds defaults until a row exists (a seed is persisted encrypted on first save).
  UI: SuperAdmin → **Sign-in** (`OnboardingSettings.razor`, wrapped in `AuthorizeView Roles="SuperAdmin"`) —
  toggles, credential fields that never echo secrets (blank = keep, checkbox = clear), live status chips, and
  the setup guides incl. the exact redirect URL to register (`{BaseUri}signin-discord`); audit
  `AuthSettingsUpdated` (secrets as set/unset). Register/Login/Provisioning read the policy, not IConfiguration.
- **External sign-in (`Web/Security/ExternalAuthEndpoints.cs`, provider-agnostic):** BOTH handlers are
  registered at startup (`AspNet.Security.OpenId.Steam` 9.0.0, `AspNet.Security.OAuth.Discord` 9.4.1 —
  scope `identify` only, PKCE, `SaveTokens=false`, `CorrelationCookie.SameSite=Lax` because the default
  None breaks on plain HTTP; external cookie `HnH.External` 15 min). **`DynamicAuthSchemeManager`** (Web
  singleton, started after Build with the loaded settings, subscribed to `AuthSettingsCache.Changed`) adds
  / removes the scheme on `IAuthenticationSchemeProvider` and evicts `IOptionsMonitorCache` so
  **`DynamicAuthOptionsConfigurator`** (an `IConfigureNamedOptions` registered AFTER AddSteam/AddDiscord)
  injects the stored key/secret on the next options build — no restart. Removing the scheme matters: the
  auth middleware initialises every registered remote handler per request and an OAuth handler with an
  empty ClientId throws (placeholders cover the disabled state). `ExternalAuthProviders` computes the live
  provider list from the cache. `GET /auth/{provider}/challenge?invite=&returnUrl=`
  carries state in `AuthenticationProperties.Items`; `GET /auth/{provider}/callback` → `IExternalUserProvisioner`
  (Services; `ExternalUsernameFactory` sanitizes persona/username to `^[a-zA-Z0-9_]{3,20}$` + `_2`/`_3`/random
  suffixes; passwordless account, `RegistrationSource` = provider, Discord fills a VERIFIED `DiscordName`)
  → in-process invite redeem → `SignInAsync` → `/` or `/tenant/select`. Linking/unlinking is a **POST with
  antiforgery** from the new `/account` page (a bare GET link endpoint would be account-linking CSRF);
  unlink refuses the last sign-in method. `ExternalAuthProviders` singleton gates every button/route.
  Login page shows "Sign in through Steam" / "Continue with Discord" above the password form.
- **SuperAdmin → Accounts** (`AccountsOverview.razor`, `GET /api/superadmin/accounts`, `AccountOverviewService`):
  summary chips (total, by sign-in method, by registration source, without a map), search + method/source/
  no-map filters, server-paged table with Sign-in icons (`SignInMethodIcons`, external id in tooltip),
  Registered (source + date), Last login, and membership chips whose tooltip says how they joined. Member
  lists (Admin → Users, superadmin tenant details) gained Sign-in + "Joined via" columns
  (`SignInMethodResolver` derives methods from `PasswordHash` + `AspNetUserLogins`).
- **Deployment:** nothing provider-related in compose anymore — SuperAdmin → Sign-in owns it;
  `SelfRegistration__Enabled=true` on the api service only seeds the first start (open registration is the
  recommended default — otherwise a no-invite newcomer cannot start a fresh map without Steam/Discord).
  Discord needs the exact redirect URI `https://{domain}/signin-discord` registered (the page shows it with a
  copy button); Caddy needs no changes (`/auth/*`, `/signin-*` default-route to Web). Security notes in
  `deploy/README.md`. Legacy pending-approval machinery (PendingUsers, 7-day purge, 1:1 invitation joins) is
  untouched and only matches pre-existing rows.
- **Tests (303/303):** `ActiveTenantResolverTests` (6), `TenantMembershipServiceTests` (8),
  `InvitationServiceMultiUseTests` (7, incl. the one-winner race and back-filled legacy rows),
  `TenantProvisioningServiceTests` (6), `ExternalUsernameFactoryTests` (15), `ExternalUserProvisionerTests`
  (5, real `UserManager` over SQLite with the app's relaxed password policy), `AccountOverviewServiceTests` (4),
  `AuthSettingsStoreTests` (10 — superadmin-only by claim+DB incl. revoked-role refusal, policy carries no
  secrets, API process never decrypts, config defaults + seed persistence, encrypted storage, keep/clear
  semantics, clamping, cache, audit).
  Gotchas recorded: Git-Bash `sed -i` silently converts CRLF files to LF; `UserManager` in tests needs the
  app's password policy or seeded password accounts silently fail to create.

### 2026-08-23: Cookbook ingredient facet (data-derived larder filter, flat view)

**The flat "All recipes" view gained an "Ingredients" facet — a compact picker whose contents ARE
the data analysis: the tenant's full ingredient vocabulary ranked by how many recipes use each
(dropdown lines read "Salt — 12,525 · 25.5%").** Closes the customer's "filter ingredients for
what's available" larder case properly. Grounding analysis (dev prod-copy DB, 49,056 variants):
only **358 distinct ingredient names, perfectly clean** (no case/whitespace/volume noise — ingestion
`MapIngredients` only trims; `NormalizeName`'s volume-prefix strip applies to FOOD names only), so
matching is exact-name `OrdinalIgnoreCase` and the whole vocabulary ships to the client; recipes
have 2–5 ingredients (868 have zero); strict-larder coverage: top-10 → 4%, top-120 → 50.9%.
- **Semantics (user-settled, revised 2026-08-23 to AND):** multi-select; default **"contains
  all"** (recipe must contain EVERY picked ingredient, extras allowed — the user asked for an AND
  operation) with an **"Only these"** strict-larder toggle (recipe's entire recorded ingredient
  list ⊆ selection). Counts are **add-one**: a name's count is what the result becomes if you
  pick it next (AND: co-occurrence with the picks; a picked tag = current result count). The
  picker dropdown ranks by overall usage with nothing picked, and by live add-one count once
  something is — in AND mode names that combine with nothing are dropped from the list (picking
  them could only empty the result). **Zero-ingredient recipes never match an active selection
  in either mode** (unknown ≠ none).
- **Core kernel `IngredientFilter`** (`Core/Cookbook/IngredientFilter.cs`, pure — Core because the
  test project can't reference Web): `Scan` (one pass per recipe, prior-index distinct guard —
  duplicate names count once; returns Hits/Missing/SoleMissing), `Matches`, `CountDistinctNames`,
  `BuildVocabulary` (rank = count desc, name asc). Selection sets MUST be built with
  `StringComparer.OrdinalIgnoreCase` (membership supplies case-insensitivity).
  `IngredientFilterTests` (10). Suite 241/241.
- **All in-circuit, no new API surface:** vocabulary memoized per `_flatGen` from `AllFlatRows()`.
  **Both views (revised 2026-08-23):** in the Ungrouped view the filter is per recipe row; in the
  Foods view a food passes when ANY of its recorded recipes passes (`IngredientFoodMatches`, a
  food-id set memoized per (rows gen, selection), applied in `BaseFiltered` so `Filtered` and
  `CountBase` both respect it). The Foods view has no recipe rows of its own, so opening the
  wall or holding a selection there makes the `OnAfterRenderAsync` funnel load the shared rows
  on demand (wall shows a loading/retry state meanwhile; the facet is simply not applied until
  they arrive). Wall chip counts are always RECIPE add-one counts (the analysis is per recipe)
  even while the Foods table counts foods. In the Foods view the name-cell hint becomes
  "N of M recipes" (matching recipes within the world bucket; the FEP-condition hint keeps
  precedence when both are active) and the expanded variations sub-table is filtered to the
  fitting recipes (`FilteredVariants`). `ClearFilters`/`ClearActiveFilters`/
  `ApplyHighlightAsync` wipe the selection from any view.
- **Counting** (inside the one-pass memoized `ComputeFlatCounts`): all other chip families gained
  an `ingOk` flag (they respect the selection); the ingredient family itself counts under every
  OTHER family — OR mode: classic contains-counts per distinct name; strict mode: **near-miss
  counting** (`Missing==0` → shared `IngredientFullMatch`; `Missing==1` → that `SoleMissing`
  name's count = "adding this unlocks N recipes"; a selected chip's own count is provably 0, so
  `IngredientChipCount` shows fullMatch = the current result count on selected chips).
- **UI (revised 2026-08-23 — the dropdown was replaced by an A–Z wall):** one slim `stat-chips`
  row after World, only when `FlatActive`: a "Browse ingredients (358)" toggle + the "Only these"
  toggle (appears when ≥1 picked). The toggle opens `.ingredient-wall` — a bounded scroll box
  (max-height 360px) holding EVERY recorded ingredient as a compact `wall-chip` grouped by first
  letter (`IngredientWallGroups`, memoized with the vocabulary), a find box (`_ingredientWallFilter`,
  substring), live count pills (overall count with no picks; add-one count with picks; "+N" unlock
  count in strict mode), picked chips `.selected`, and **dead chips** (`IsDeadPick`: AND mode +
  picks + zero add-one count → `disabled` + `.empty`, because picking them could only empty the
  result; strict mode never disables). The wall stays open across picks. A `MudAutocomplete`
  dropdown was tried first and rejected by the user as unusable at 358 names ("a lot of them is
  the complexity"). **Picked ingredients are plain tags in the existing Filters row** (user
  request) — warm-wheat `--chip-color: #e8ddc9`, `×` pill, tooltip = live count + global
  count/share, click removes; they count into `ActiveFilterChipCount` (flat view only) so the
  Clear chip appears. Data note for future grouping work: only 74/358 ingredient names are
  catalog foods (usable satiation groups), wiki categories for the rest are maintenance noise,
  and recipe-slot inference from canonical recipes fails (109/931 foods have generic slots,
  assignments confounded) — a categorized "pantry panel" would need a curated name→category map.
- **Second source — recipe components (2026-08-23, "why doesn't butter appear?"):** the game
  client records only the variable, quality-carrying SOURCE ingredients and **flattens
  intermediates** — every butter dish is recorded as the milk the butter was churned from
  (Aurochs/Cow/Goat/Sheep milk), pancakes as flour+egg — and **fixed recipe parts (leeks,
  trout filets, cavebulb, bat wings) are never recorded at all**. "Butter" therefore occurs in
  zero of the 49k recorded recipes; it exists only in the wiki canonical recipe text. The wall
  now merges a second vocabulary: **component names from each food's canonical recipe tree**
  (`ComponentsByFoodId`: direct parts + "made from" chain links + nested intermediates via the
  same `BuildRecipeNodes` expansion the detail panel uses, prep-variants inheriting their base
  recipe; `ComponentJunkRegex` strips wiki quantity leftovers incl. a trailing " x";
  `ComponentDenylist` + patterns drop non-ingredient "requires" — containers/tools/stations,
  crafting materials pulled in by deep expansion (Board/Log/Bucket/String), "X or Y" tool
  alternatives, "…Dungeon" locations, the quest item, the "Creatures" category word, and Water).
  Only names the recorded data never shows become component chips (dashed, paler wheat,
  `.wall-chip.component`, legend line in the wall explains the distinction). Component picks
  (`_selectedComponents`, own dashed Filters-row tags) filter at **food level** ("the recipe calls
  for it", catalog + recipe index only — no rows needed in the Foods view) and AND with recorded
  picks in both views; counts are add-one like everything else (`FlatChipCounts.Components`,
  other families see `ingOk && compOk`). Generic slot tokens (Spices, Raw Meat, Any Flour,
  Edible Mushroom…) are deliberately kept as pickable components. Verified: Butter → 25 foods
  (9 direct dishes + everything whose tree reaches butter via batter/pancake/dough
  intermediates); Butter + Cowsmilk → 852 recipes / 22 foods, all rows containing Cowsmilk.
- **Verified live** (dev DB): pick Salt → exactly 12,525 of 49,056, all rows contain Salt, Meat
  satiation count 14,887→4,840; +Chives OR-union → 16,199; "Only these" with {Salt, Chives} → 720
  single-seasoning roasts, every row ⊆ selection, chips show 720; STR chip composes (9,169,
  ingredient counts re-scope); world switch rebuilds rows in ~530ms with selection intact;
  Foods-view round trip preserves the larder; Clear wipes it. After the AND revision: Salt +
  Chives → 951 (= the measured pair overlap), dropdown ranks co-occurring names first.

### 2026-08-22: Cookbook flat "All recipes" view (ungrouped food items)

**/cookbook gained a View toggle — "Foods" (the existing one-row-per-food table) vs "Ungrouped"
(chip label; was "All recipes" until the user renamed it on 2026-08-23 — `CookbookView.Recipes`):
every recorded recipe variation as its own top-level row (~49k rows/tenant), Cediner-style, default
sort best-Total first.** Users wanted to see food items individually instead of the "N recipes"
nesting; the design question was the filters — resolution: **every facet applies per recipe row**.
- **Filter semantics in flat mode:** text search matches food name/recipe-text (parent blob) OR the
  variation's own ingredient list — so "fairy mushroom" finds 276 recipes where grouped mode finds 1
  food. **Comma-separated search terms AND together** ("pork, mushroom" = rows matching both, the
  customer's "filter ingredients for what's available" case; comma-free text stays one exact
  phrase) — `SearchTerms` helper, applied in all three search sites: grouped `BaseFiltered`, flat
  `FlatCountCore` (per term: parent blob OR variant blob — generic terms like "mushroom" also hit
  the canonical recipe text, so Chantrelles-variants still match), and the per-food variations
  filter box. FEP threshold conditions evaluate **exactly per row locally** (`TargetOf(VariantRow)`, no
  `/filter-matches` round trip — all variants are in-circuit; the "N of M recipes" hint is
  grouped-only); satiation/prep/New(7d) inherit from the parent food; world uses the variations
  sub-table's bucket semantics (`WorldMatches(variant.Worlds, …)`) with world-effective values;
  panels filter by parent food name; a focused panel chip filters to the food and pins the exact
  variant first (`variant-focused` highlight). Chip counts count **recipes** in this mode (tooltips
  say so via `CountNoun`), header shows "X of Y recipes", `⊞ Columns`/quality scaling/pill+cell
  click-to-filter tools all work per row. Notification highlight deep-links force the Foods view.
  Each row: star/drag/add-to-panel with the variant signature (favorites interop unchanged) + a
  **ReadMore "Food details" button** that switches to Foods view focused on that food with the
  variant pinned (row click deliberately does nothing — no expansion in flat mode).
- **Data path:** new `GET /api/v1/cookbook/variations` (auth-only, same group as /foods) →
  `FoodCatalogService.GetAllVariationsAsync` (ambient tenant guard + query filters; deliberately
  uncached server-side; shared `MapVariantDto`/`ResolveContributorNamesAsync` extracted);
  `FoodVariantDto` gained `FoodId`. Response is tens of MB → the page fetches via the **`APIUpload`**
  client (default `API` client's 10s resilience timeout would cancel+retry it).
- **Cross-circuit cache (`CookbookFlatCache`, Web singleton):** Blazor Server retains disconnected
  circuits for minutes, so per-circuit copies of ~50k built rows would multiply ~100MB per refresh.
  Rows are built once per (tenantId, world-genus) and shared; circuits keep references only
  (memoized filtered list). Keyed by the user's `TenantId` **claim** (same claim the API resolves —
  cache can never cross tenants); fetch delegate comes from the page (auth cookie is scoped), one
  in-flight fetch shared via `Lazy<Task>`, 3-min refresh, 10-min idle eviction, failed fetch drops
  the entry (page shows Retry). `CookbookVariantRow` + builder moved to
  `Web/Services/CookbookFlatCache.cs` — the page's sub-table `VariantRow` is now a `@using` alias
  of it, `BuildVariantRow` delegates. Foods missing from the bulk list (added after the fetch, or
  variant-less) get a synthetic row from canonical values (empty signature = whole-food favorites).
- **Perf:** the grouped view's recompute-per-render LINQ doesn't scale to 49k, so the flat pipeline
  is **memoized by a filter-state key** (`FlatStateKey` — every input incl. `_flatGen` bumped on
  entries/RebuildRows and `_panelFilterVersion` bumped wherever `_activePanelNames` changes) and all
  chip counts are computed in **one pass** with per-family exclude-own-facet semantics
  (`ComputeFlatCounts`, mirroring `CountBase`). Measured in dev against the prod-copy DB: facet
  toggle ≈140ms round trip, header re-sort ≈230ms (MudTable sorts the memoized list), Web process
  ≈190MB private with all rows loaded. World switches rebuild via `OnAfterRenderAsync` funnel
  (`FlatRowsCurrent` world-key check → `EnsureFlatRowsAsync`), showing the skeleton meanwhile.
- **Tests (231/231):** `FoodCatalogServiceAllVariationsTests` (4 — real SQLite with a
  DefaultHttpContext-backed accessor so the ambient tenant filter is ACTIVE: tenant scoping,
  FoodId set + per-food ordering, contributor resolution incl. unknown→"unknown", empty without
  tenant context, per-food endpoint also carries FoodId). UI verified live (login → toggle →
  str>50% =3,280 rows, +Meat =1,051, search, sort, columns, details-switch, favorites star).

### 2026-08-22: WebP pyramid consistency (fixes prod black tiles at max zoom-in + ghost imagery)

**Root cause of "black tiles at ?z=6/7 that show content zoomed out" (prod, tenant
sketchbook-pot-u-2046, live map 2047 = 1953 merged at +50,−9): the 400×400 WebP pyramid the viewer
serves had NO consistency mechanism.** Files were only overwritten by gridUpload-driven
force-regeneration (in-memory queue, bounded 4096 DropOldest, lost on restart); the background
scans only gap-filled missing files; merges/imports/wipes never touched the pyramid at all; and a
Jan–Mar 2026 era (`6034628`→`a45f853`) deleted the whole z0–z6 chain on every upload, leaving
permanent holes wherever regeneration failed. Cells whose zoom-0 rows are gone (wiped/moved by
frame flip-flops and merges) 404 at server z0/z1 (= Leaflet z7/z6, black), while stale z2–z6 files
ghost the old imagery. Fixes (221/221 tests):
- **`ForceRegenerateLargeTileAsync` null → deletes the stale disk file** (emptiness propagates; a
  transient generation error — distinguished from "no sources" — never deletes).
  `ZoomTileProcessorService` now walks the z1–6 parent chain even when z0 regen returns null, so
  wiped cells stop ghosting at far-out zooms. `InvalidateTenantCache` (was a no-op) and new
  `InvalidateMapCache`/`DeleteMapWebpTiles` do real prefix eviction of the static caches.
- **Durable backstop** (superseded 2026-10-11 by the `DirtyWebpCells` table and its single consumer —
  reading a queue the legacy rebuild also deletes from lost marks): the 5-min dirty scan
  (`LargeTileGenerationService`) now also derives WebP
  cells from zoom-1 `DirtyZoomTiles` rows (`WebpDirtyCellPlanner`, pure) and re-enqueues any cell
  whose z0 file is older than its newest mark or missing-with-rows — freshness = file mtime vs row
  CreatedAt, so nothing loops; rows are still consumed by the legacy `ZoomTileRebuildService`.
  Queue headroom capped at 3000 so backfill can't flush live-upload requests. New
  `ITileService.MarkParentTilesDirtyBatchAsync` (deduped diff-insert) feeds it from bulk writers:
  **hmap import + public-map import now mark dirty rows** (they bypass TileService.SaveTileAsync
  and previously left the pyramid permanently stale where files already existed).
- **`MergeMapsAsync`** now deletes the source map's tile rows (all zooms — merges used to orphan
  the source's whole tile set: ~12k dead rows across 14 deleted maps in prod, reconstructed via
  shared-File refs 1965→1953→2047) plus its per-map tile dirs (legacy + `large/`; grid PNGs are
  shared, never touched), and enqueues WebP regen for the merged-in target cells. `wipeTile`
  (DeleteMapTileAsync) enqueues its cell too.
- **Region-wipe tool:** deletes every WebP file (z0–6) whose footprint intersects the box (result
  field `WebpTilesDeleted`) + evicts both processes' caches (new Web internal endpoint
  `/internal/tile-cache/invalidate-map`), and its grid-PNG deletion is now **refcount-guarded** —
  tile Files are grid-id-keyed and shared by flip-flop/merge twin rows; a file still referenced by
  any out-of-box row survives (deleting shared files was the tool's latent data-loss bug).
- **Repair endpoint: `POST /api/superadmin/tenants/{tenantId}/maps/{mapId}/rebuild-webp-tiles`**
  (body echoes `confirmMapId`) — deletes the map's whole `large/` pyramid, evicts caches in both
  processes, seeds regeneration (dirty rows for every 4×4 cell with zoom-0 rows + queue fast
  path), bumps mapRevision, audits `SuperAdminRebuiltWebpTiles`. Viewers keep working during the
  rebuild (on-the-fly generation). This is the fix for the existing prod drift — run it per
  affected map; genuinely-wiped areas then read honestly empty at all zooms until re-mapped.
  **UI:** wand button per row in SuperAdmin → Maps & Markers → All Maps (`GlobalMapsViewer.razor`),
  confirm box → POST via the `APIUpload` client (default `API` client would 10s-timeout) →
  snackbar with counts. Browsers cannot call `/api/superadmin/*` directly — Caddy routes generic
  `/api/*` to the Web service; superadmin actions only work through the Blazor server-side clients.
- **Map Integrity = the one repair console** (user request "have it all inside the map integrity
  tool"): the scan (`MapIntegrityService`, now also injected with IConfiguration +
  IStorageQuotaService) additionally reports **orphaned storage per tenant** — tile rows on dead
  map ids, dead per-map tile directories (legacy `tenants/{t}/{mapId}/` + WebP
  `tenants/{t}/large/{mapId}/`, sized from disk), and unreferenced pool PNGs in `grids/`
  (referenced by no live-map tile row and no grid row). New
  `POST /api/superadmin/integrity/tenants/{tenantId}/purge-orphans` (body echoes
  `confirmTenantId`, audit `SuperAdminPurgedOrphanedMapData`): deletes dead rows FIRST, then dead
  dirs, then unreferenced PNGs, then `RecalculateStorageUsageAsync`. **Safety guards:** a dead
  map's dir is KEPT (with warning) if live rows reference files inside it (public-map imports
  write zoom-0 PNGs into per-map dirs and merges copy rows with File unchanged — deleting would
  blank live imagery); pool PNGs are kept if a grid row with that id exists (PNG can land on disk
  before its tile row commits); **the reference set counts only LIVE-map rows** (a dead map's own
  rows must not protect its files, or scan reports nothing while purge deletes — the exact
  scan/purge asymmetry a test caught). `MapIntegrityPanel.razor` gained the orphan table
  (counts-explicit confirm → `APIUpload` POST → rescan) and a per-issue-map "Rebuild WebP tiles"
  button. Prod context: sketchbook-pot-u-2046 carries ~12.5k dead zoom-0 rows / ~61 MB dead legacy
  pyramids (merge chain 1957/1962/1963/1965/1966→1953→2047) — the purge replaces the planned
  manual `rm -rf` + sqlite3 entirely.
- **WebP drift detection ("how do we know a map needs rebuild?"):** the scan also measures every
  live map's pyramid file-by-file against its zoom-0 rows (`ScanWebpDriftAsync`): a zoom-z file at
  (X,Y) covers base [X·4·2^z, (X+1)·4·2^z) — footprint with NO rows → **ghost** (renders
  deleted/moved terrain), file mtime + 30-min slack older than the newest row in footprint →
  **stale** (missing newer content); missing z0 files are informational only (on-view generation
  heals them — *corrected 2026-10-11: only where their source PNGs still exist; cells whose sources
  are gone are reported as `MissingTileSources`*). Per-zoom newest-data aggregates are built by halving cell dictionaries upward; ~1
  DB query per tenant + one dir enumeration per map. **`Tiles.Cache` is unix MILLISECONDS on
  upload/merge/zoom paths but SECONDS on the hmap-import path** — `CacheToUtc` normalizes
  (< 10^12 → seconds); don't compare Cache values without it. Only ghost/stale maps are reported
  (`WebpPyramidDriftDto`, sample tiles capped at 8). **Derivation rule:** a parent file older
  (+slack) than any of its four child files is also stale — pre-fix chain regens baked ghost
  content from stale siblings into parents whose mtimes beat the raw data; only the child
  comparison exposes those. **Known limitation (metadata-only detector):** a file NEWER than both
  its footprint data and its children can still carry wrong content baked in during the broken
  era (mixed live/dead footprint, regenerated post-damage) — prod map 2047 is exactly this and
  passes the scan clean; such known-incident maps need one manual rebuild (All Maps wand), after
  which the fixed pipeline keeps them correct.
- **Rebuild-all ("superadmin control of all"):**
  `POST /api/superadmin/integrity/rebuild-webp-drift` (body `{confirmAll:true}`, audit
  `SuperAdminRebuiltAllDriftedWebpTiles`) — reruns the drift scan SERVER-side (never trusts a
  client-supplied list) and rebuilds every flagged map via the shared `RebuildMapWebpCoreAsync`
  (extracted from the per-map handler: delete pyramid + both-process cache eviction + dirty-row
  seeding + queue fast path with shared 3000 headroom + revision bump). Any number of maps
  completes via the 5-min backstop even when the queue fills. Panel: drift table with per-map
  rebuild buttons + one "Rebuild all" button.
- **Proactive detection:** new `MapIntegrityCheckService` (Api, hosted): daily (config
  `IntegrityCheck:Enabled`/`IntervalHours`, 15-min startup delay) runs the full scan and logs one
  WARNING summary (contested maps + worst offender, placeholder rows, orphan tenants + reclaimable
  MB, drifted maps + worst offender) when anything is found — surfaces in Loki/Grafana without
  opening the tab. Detection only; repair stays a human click.
- **Fixed in passing:** `TileService.MarkParentTilesDirtyAsync` used truncating `/2` — every
  negative base coord marked the wrong parent (e.g. −1 → 0), so the legacy rebuild refreshed the
  wrong tiles in all-negative quadrants. Now uses floor (`Coord.Parent()`).
- **Tests (226/226):** `LargeTileServiceTests` (5 — null-deletes-file / error-keeps-file / cache
  eviction / pyramid delete), `WebpDirtyBackstopTests` (5 — batch marking incl. negatives +
  planner), region-wipe +2 (shared-file survives, covering webp deleted), merge regression
  (source rows gone + cells queued), `MapIntegrityServiceTests` +5 (orphan scan zoo /
  purge-deletes-only-dead-data incl. quota recalc / dead-dir-referenced-by-live-rows kept /
  drift zoo ghost+stale+missing+fresh / seconds-unit Cache normalization + clean-map absence).
  Known leftover: `SetCoordinatesAsync` still doesn't move tile rows (separate task chip).

### 2026-08-17: Map-grid write-path hardening + region-wipe repair tool (corruption incident)

**Root-caused the live map-corruption incident (tenant sketchbook-pot-u-2046, map 1953): a
Merge-mode .hmap import planted a region in a wrong coordinate frame; live gridUpdates then
flip-flopped between frames via silent last-wins anchoring → 39 double-claimed cells, tiles
overwriting each other on every visit, a Thingwall marker stuck over foreign territory, player
dots teleporting.** All three Grids write paths are now guarded; suite went 158 → 200 tests.
- **gridUpdate (`GridService.ProcessGridUpdateAsync`):** placeholder cells (null/empty/`"0"` —
  the client's not-yet-loaded marker) are holes — never anchor, never persisted; all-placeholder
  matrix = no-op (previously a stored `"0"` row hijacked every partially-loaded matrix as a false
  anchor — two tenants carried such rows). **Per-map offset-consistency vote:** known grids of one
  map disagreeing on the implied offset → the WHOLE update is rejected with zero writes + one
  forensic warning ("GridUpdate REJECTED …" with the full matrix and every anchor's stored
  coord/implied offset); empty-but-shape-valid response (client just resends seconds later).
  **Occupied-cell guard:** inserts skip destination cells owned by a different grid id (one
  batched range query via new `GridRepository.GetGridsByMapInAreaAsync`, rides existing
  `IX_Grids_Map_CoordX_CoordY` — no migration; built with TryAdd because corrupted DBs hold
  duplicate cells). **Two-witness merge rule:** merges (irreversible — source map deleted) only
  run between maps each witnessed by ≥2 agreeing grids in THAT matrix (`MIN_MERGE_WITNESSES`);
  lone-witness merges defer to a later matrix ("Map merge deferred" warning). Single-anchor
  frontier mapping is deliberately unaffected; pass 2 now reuses pass-1 lookups (−9 queries).
- **Hmap import (Merge mode):** decision logic extracted to public **`HmapMergePlanner.Compute`**
  (pure, unit-tested at HmapData level). Votes per **(map, offset) group** — the winner defines
  target map AND offset atomically (before: offset voted across ALL maps but
  `targetMapId = matches.First()`, so a segment could merge into map B with map A's frame). The
  dominant segment now needs `MIN_MERGE_MATCHES = 5` agreeing matches (before: merged
  unconditionally on any match count — the incident's seed) and, like every segment, passes a
  **pre-plant conflict scan**: any to-be-planted grid landing on a cell owned by a different id
  (in DB or claimed by an earlier segment of the same import) → whole segment falls back to
  CreateNew (`CoordConflicts`). Sentinel `GridId == 0` grids never match/count/import (the second
  `Id='0'` ingestion path). The dead zero-validation single-anchor fallback in
  `ImportSegmentAsync` is DELETED. New `HmapImportResult` counters `BelowMinMatchesAsNewMaps` /
  `CoordConflictsAsNewMaps` (enum-driven; reason strings no longer sniffed). Fixed in passing:
  the old per-map coord lookup used `ToDictionary`, which THREW on the duplicate cells a
  corrupted DB contains — Merge imports for an affected tenant would have crashed outright.
- **Public-map import:** same occupied-cell guard before planting grid rows (~PublicMapTenantImportService
  line 300; the tile is still written — tiles are coord-keyed); skip count exposed as
  `PublicMapImportResult.GridRowsSkippedOccupiedCell` + summary warning.
- **Superadmin region-wipe repair tool** (for surgically cleaning corrupted areas):
  `GET /api/superadmin/tenants/{tenantId}/maps/{mapId}/wipe-region/preview?x1&x2&y1&y2` (counts,
  % of map, map extent — blast radius before committing) and `POST …/wipe-region` (body must echo
  `confirmMapId`, purge-style speed bump) → ONE transaction deleting in-box marker-attached
  Timers (TimerWarnings cascade) → Markers (chunked by 500) → OverlayData → zoom-0 Tiles (rows +
  best-effort file deletion with the path-containment guard) → Grids; then map-revision bump +
  tile-cache invalidation + audit `SuperAdminWipedMapRegion`. Zoom 1–6 tile rows deliberately
  kept (stale imagery heals as re-uploads regenerate the pyramid); CustomMarkers/Roads/Pings
  deliberately not wiped. New `IMapRegionWipeService`/`MapRegionWipeService`
  (TenantDataPurgeService pattern: every query `IgnoreQueryFilters()` + explicit TenantId).
- **Migration `RemovePlaceholderGridRows`** (raw SQL, cross-tenant on purpose): deletes
  `Markers WHERE GridId='0'` then `Grids WHERE Id='0'`. Verified by applying the real pipeline to
  a dev-DB copy via `dotnet ef database update --connection` — exactly the 2 known rows deleted.
- **Tests (200/200):** `GridServiceHardeningTests` (15 — placeholder/reject/occupied-cell/witness
  gating/frontier regression), `HmapMergePlannerTests` (12 — incl. the D6 First()-map regression
  and cave/proximity preservation), `HmapImportServiceMergeTests` (3 — end-to-end through
  ImportAsync with synthetic binary .hmap files; version-1 grids carry no tilesets → offline),
  `PublicMapTenantImportOccupiedCellTests` (2), `MapRegionWipeServiceTests` (10 — real SQLite
  file, DbContext without IHttpContextAccessor like a superadmin request; Timers need a seeded
  AspNetUsers row). Three existing `GridServiceMapMergeTests` reseeded with second witnesses —
  one of them had internally-disagreeing witnesses and only passed because of the last-wins bug.
- **Superadmin "Map Integrity" UI** (SuperAdmin panel, new tab before System):
  `GET /api/superadmin/integrity/scan` (`IMapIntegrityService`/`MapIntegrityService`, read-only,
  IgnoreQueryFilters — one GroupBy/HAVING aggregate over Grids) returns
  `MapIntegrityReportDto`: per (tenant, map) the contested-cell count, bounding box and up to 12
  sample cells with the grid ids fighting over each, plus any legacy placeholder ("0") rows.
  `MapIntegrityPanel.razor` auto-scans on open (Rescan button), expandable per-row cell details,
  and a per-issue repair action opening `WipeMapRegionDialog.razor`: padding input (default 2) →
  live preview via the wipe-region preview endpoint (counts, % of map, extent; red warning above
  50% of the map) → type-the-map-id confirm (purge-dialog pattern, `Immediate="true"`) → POST via
  the `APIUpload` client → snackbar with counts + up to 3 warnings → parent rescans. DTOs in
  `Core/DTOs/MapIntegrityDtos.cs`; `MapIntegrityServiceTests` (5, real SQLite).
- **Ops notes:** a tenant whose stored anchors permanently disagree now REJECTS every matrix
  touching both frames until repaired (that is what the wipe-region tool / Map Integrity tab is
  for) — watch for "GridUpdate REJECTED" / "Skipped grid insert" / "Map merge deferred" warnings.
  Legitimate overlaps of <5 shared grids now import as new maps instead of merging (visible via
  the new counters). Client-side matrix hygiene (Hurricane/KamiClient around teleports) remains
  the one unfixable-server-side seed: a matrix with exactly ONE known-but-stale grid cannot be
  cross-checked.

### 2026-08-17: VPS disk fix — Docker log caps + Watchtower --cleanup (deploy)

**/var/lib/docker hit 104G on the VPS: 52GB was container json logs (no rotation anywhere — top
offenders 7–13GB each), the rest overlay2 bloat from 550 accumulated images (Watchtower had no
`--cleanup`, so every CI deploy left the old image behind).** Fixes in deploy/docker-compose.yml +
docker-compose.yml.example: an `x-logging: &default-logging` anchor (json-file, max-size 50m,
max-file 3 → ≤150MB per service) applied via `logging: *default-logging` on every service, and
`--cleanup` added to the Watchtower command. Compose files are NOT auto-deployed (Watchtower only
swaps images) — they must be copied to `/opt/hnhmap` on the VPS and applied with
`docker compose up -d`, which recreates containers whose config changed; recreate Caddy separately
at a quiet hour (it owns ports 80/443 — recreation drops all users for a few seconds). Note: the
observability configs referenced by compose (`./observability/*.yaml` — otel-collector, prometheus,
loki, tempo, grafana provisioning) live only on the server, not in the repo. Emergency log
truncation used on 2026-08-17 (`truncate -s 0` on `*-json.log`) freed the 52GB immediately.

### 2026-08-16: Overlay zoom gate (fixes /map browser freeze + overlay request storm)

**Zoomed-out /map views with any claim/village/province overlay toggle enabled froze Chrome and
self-DoS'd the server.** Overlay data is fetched per 100×100 grid, and one 400px canvas tile covers
scaleFactor² grids (scaleFactor = 2^(HnHMaxZoom−z)·4 → 4 at max zoom, **256 at min zoom**), so a
zoomed-out viewport enumerated >1M grid coords → 10,000+ fire-and-forget batches of 100 coords
(`JsRequestOverlays` interop → Web→API HTTP each), and every response ran a per-grid-per-tile
repaint scan (~1.3M iterations at min zoom). Billions of iterations + a flooded Blazor circuit =
frozen tab and "10000+ hidden console messages" (per-batch `console.debug`). **Worst case was maps
with NO overlay data** — every batch returned 0 rows and still paid the full scan. Overlay toggles
default off but persist in localStorage, so one past toggle-on = a storm on every visit. Fixes,
all in `Web/wwwroot/js/map/overlay-layer.js`:
- **`MAX_OVERLAY_SCALE_FACTOR = 16` zoom gate:** `createTile` returns a blank canvas — no grid
  enumeration, no fetching, no `activeTiles` registration — when zoomed out past the top 3 zoom
  levels **or when no overlay types are enabled** (previously it enumerated/rendered even with all
  types off). Toggling types / zooming triggers `redraw()`, which recreates tiles, so re-entry into
  gated range refetches naturally. Visual trade-off: overlays simply don't render at far-out zooms
  (a claim is <1 grid = <25px there anyway); raise the constant only with a per-map overlay index
  (see follow-up below).
- **`setOverlayData`:** early-return on empty responses (coords stay `_pending`, preserving the
  don't-re-request behavior); the repaint scan now iterates the ≤100 returned overlays against each
  tile's grid range instead of every grid of every tile.
- **`invalidateOverlayAtCoord`** (SSE `overlayUpdated`, fires for every grid a game client
  uploads): still invalidates the cache entry but skips the immediate refetch while the zoom gate
  is active — zooming back in refetches via `createTile`.
- **`MAX_PENDING_FETCH_COORDS = 3000`** safety cap in `requestOverlays`; capped coords are left
  unmarked (not `_pending`) so later redraws can retry them.
Follow-up (not built): a per-map "grids that have overlay data" index endpoint would make low-zoom
overlay rendering affordable and let empty maps answer with one response instead of N batches.
Separate issue spotted, not fixed here: maps missing webp zoom tiles (e.g. map 1896 z5/6) re-404
the whole viewport after every `mapRevision` bump because the revision refresh (since 2026-10-11
`SmartTileLayer._runRevisionRefresh`) clears the map's entire negative cache before refreshing.

### 2026-08-15: Cookbook toolbar/panels no longer sticky (user request)

**The /cookbook toolbar (search/filters) and panels bar now scroll away with the page.** The sticky
stack covered half the screen when pinned over the table and the user found it annoying. Removed: the
`.sticky-stack` wrapper div in Cookbook.razor, its `position: sticky` CSS + every `.ck-stuck` rule
(slit `::before`, toolbar margin/radius swap, panel-card condensing) in Cookbook.razor.css, and the
whole pin-detection/condense block (scroll/resize/MutationObserver + height-compensation margin) in
cookbook-dnd.js. This supersedes the ck-stuck parts of the 2026-08-11/12 glassmorphism entries below
(the `.glass-morphism`/`.panel-card` oversized-`::before` blur pattern itself stays — that rule is
still load-bearing). Don't reintroduce stickiness here without the user asking.

### 2026-08-15: New-foods notifications overhaul (live SSE everywhere + coalescing digest + cookbook deep-link)

**CookbookFoodAdded went from a dead DB row (no SSE, no click action, one row per ~10s client flush,
never expiring) to a live, actionable, coalescing digest with a stat preview.**
- **Coalescing (anti-spam core):** new `CookbookNotificationService` (Services; scoped) replaces the
  endpoint-local digest in ClientEndpoints. One rolling tenant-broadcast row: while the latest unread
  `CookbookFoodAdded` digest is <15 min old (sliding window keyed on `CreatedAt`, which is **bumped on
  every merge** — floats to bell top AND keeps the window query on the existing `(TenantId, CreatedAt)`
  index), new foods merge in place; read/aged/legacy(-null-ActionData)/newer-schema rows → fresh row.
  Per-tenant `SemaphoreSlim` serializes concurrent flushes; the whole method never throws (upload must
  never fail). `ExpiresAt = lastMerge + 14d`. **`ActionData` is the single source of truth**
  (`CookbookNotificationActionData` in Core/NotificationDtos.cs, serialized camelCase with an explicit
  serializer — the outer HTTP serializer does NOT re-case nested JSON strings): schemaVersion,
  totalCount (uncapped), foodIds(≤50)/foodNames(≤20)/worlds(≤8 genus)/contributorNames(≤10)/
  previews(≤8: id, resourceName, energy, hunger, feps), variantCount; Title/Message are rebuilt from it
  on every create/merge (world tag via `GameWorlds.DisplayName` only when single-world; multi-contributor
  phrasing "A and B" / "A, B and N others"). `ActionType = "NavigateToCookbook"`.
  Data plumbing: `FoodUploadResultDto.NewFoodDetails` is `[JsonIgnore]` (game-client response shape
  unchanged); `IngestClientRecordsAsync` collects new `FoodEntity`s (ids committed per-food, so they
  survive the conflict-recovery `ChangeTracker.Clear()`).
- **SSE broadcast moved into `NotificationService.CreateAsync`** — every notification type is now live;
  the two hand-rolled broadcast blocks in `TimerCheckService` were REMOVED (leaving them = double toasts).
  Discord webhook still fires only inside CreateAsync → merges never re-ping Discord (🍳 emoji case added).
  New **`notificationUpdated`** SSE event (same `NotificationEventDto` payload) = silent client upsert —
  distinct from `notificationCreated` so merges never toast. The 4 notification channels in
  `UpdateNotificationService` are now **bounded (256, DropOldest)** — subscribers have no unsubscribe and
  the always-on stream would otherwise leak unbounded buffers on dead connections.
- **Dedicated stream `GET /api/notifications/stream`** (NotificationEndpoints; auth-only — deliberately
  NOT the Map-permission-gated /map/updates): subscribes only the 4 notification channels, zero DB work,
  tenantId from `HttpContext.Items["TenantId"]`, same tenant/user filter as /map/updates, 500ms drain +
  keep-alive. Browsers can't reach `/api/*` on the API service, so it ships with BOTH: a Web-side SSE
  proxy in Web/Program.cs (dev path + prod fallback; `HttpCompletionOption.ResponseHeadersRead` is
  load-bearing — anything else lets the resilience handler sever the stream) and a Caddy `@notifsse`
  rule **before `encode gzip`** in deploy/Caddyfile + Caddyfile.example (gzip buffering stalls SSE).
- **notification-center.js owns its own EventSource** (`STREAM_URL` const) — the old
  window.mapUpdates piggyback (worked only on fresh /map loads, died on navigation) is gone. Listeners
  attach inside `connect()` on each new instance; browser-native retry while CONNECTING, manual 1s→30s
  backoff when CLOSED (non-200, e.g. expired cookie); `dispose()` keeps the ES (component remounts just
  re-swap the .NET ref); `OnStreamReconnected` → silent refetch. **All payload reads are camelCase now**
  (the old PascalCase reads meant browser notifications/sounds/read-sync had NEVER worked). Sound default
  is `/sounds/ping.wav` — the referenced mp3s never shipped. **`CookbookFoodAdded` is deliberately
  soundless** (`SILENT_TYPES` in notification-center.js: in-app sound skipped AND the OS notification is
  created `silent: true`, which would otherwise play the system chime) — toast + bell + badge only.
- **Bell (NotificationCenter.razor):** upsert-by-Id on created (reconnect redelivery), silent
  `OnNotificationUpdated` keeping list position (badge only moves on genuine read-state flips), refetch
  on menu open (`MudMenu OpenChanged`), list capped at 50, Restaurant icon + Success toast severity,
  click → `/cookbook?highlight={ids}&hlworld={genus}`. **Stat preview** on digest rows: real food icons
  (shared `FoodIcons` helper — local `wwwroot/gfx/invobjs/*.png` (~2000 ship) with
  havenandhearth.com/mt/r fallback, same as the cookbook table; since 2026-10-11 `Core/Cookbook/FoodIcons`
  sends icons the `ShippedIcons` manifest says do not ship straight to the renderer; the bell uses `RemoteFallbackOrHide`,
  which hides the img when both sources fail instead of showing a broken glyph. The remote fallback
  needed `https://www.havenandhearth.com` added to the CSP `img-src` in deploy/Caddyfile{,.example} —
  it had been silently CSP-blocked in prod for the cookbook table too) + FEP pills colored via **`FepPalette`
  (moved from Cookbook.razor's @code to Core/Cookbook so both share it** — Cookbook's `StatColor`/
  `StatFullNames` now delegate) + energy/hunger, rendered from ActionData previews (no fetch), memoized
  per (id, raw-json). Scoped CSS: NotificationCenter.razor.css (#33322e on pastels, ≥4.5:1).
- **Cookbook deep-link:** `[SupplyParameterFromQuery] highlight/hlworld`, applied in
  **`OnAfterRenderAsync`** (page prerenders: stripping the query in OnParametersSet would erase it before
  the circuit sees it, and JS interop is illegal there; also covers clicking while already on /cookbook).
  **Stale-catalog guard:** when already on /cookbook, Blazor reuses the page instance, so `_rows` predates
  the discovery — if any highlight id is unknown, `ApplyHighlightAsync` refetches via `LoadCatalogAsync()`
  before resolving (the deep link always lands on the current catalog).
  Activation clears conflicting facets (search/filter/stat/satiation/prep/sort/focus/panel/NewOnly) and
  switches world only when needed (keep if all matches satisfy it, else hlworld if ALL matches contain
  it, else null — always via `SetWorld`); `BaseFiltered` override (before the focus branch) pins the
  table to the ids; dismissible Success chip in the panels bar; `RowClassFunc` → `.ck-new-flash` green
  tint + 2-cycle pulse (reduced-motion safe) + `cookbookHighlight.reveal()` (cookbook-dnd.js,
  scrollIntoView center); **one-shot**: query stripped via `NavigateTo(replace: true)` after apply
  (`_appliedHighlightCsv` resets when the param goes null so the same digest can re-apply).
- **"New" recency:** name-cell + detail-panel `New` badge and a "New (7d)" facet chip (filters AND
  orders newest-first, `_selectedStat` precedent) — predicate `ContributedByName != null && ImportedAt >=
  now-7d` (**contributor check excludes admin bulk imports**, which reset ImportedAt for the whole
  catalog; snapshot imports restore original dates so they stay correct); cutoff frozen per catalog load;
  chip row hidden when nothing is new; wired into ActiveFilterChipCount/ClearActiveFilters/ClearFilters.
- **`DeleteExpiredAsync` was broken for background use** — the global tenant filter reads
  `HttpContext.Items["TenantId"]`, which is null in hosted services → filter became `TenantId == NULL` →
  deleted nothing, ever. Fixed with `IgnoreQueryFilters()`, returns deleted ids; new
  `NotificationCleanupService` (30-min, PingCleanupService pattern) deletes expired + legacy
  no-expiry CookbookFoodAdded rows (>14d) and broadcasts `NotifyNotificationDismissed` per id
  (verified live on the dev DB: purged 91 expired + 1 legacy on first run).
- **Gotchas recorded:** MudBlazor components swallow unknown parameters into `UserAttributes`, so a
  typo'd parameter name compiles clean and silently does nothing — verify param names against the
  package XML docs (`~/.nuget/packages/mudblazor/8.13.0/lib/net9.0/MudBlazor.xml`); nested-JSON columns
  (ActionData) need their own camelCase `JsonSerializerOptions`.
- **Tests:** `CookbookNotificationServiceTests` (11 — real SQLite, DbContext built WITHOUT
  IHttpContextAccessor to prove no ambient-tenant reliance; real UpdateNotificationService so emitted
  events are asserted: create/merge/window-expiry/read→new-row/multi-contributor/caps/tenant-isolation/
  legacy-row/world-tag/unknown-user/empty-burst) + `NotificationServiceTests` (3 — broadcast-on-create,
  Title/Message truncation, DeleteExpiredAsync tenant-filter bypass). 147/147 green.

### 2026-08-14: Live map list + map-switching overhaul (/map viewer)

**Newly created maps now appear in the map selector without a browser refresh, and switching maps
fully reloads per-map state.** Root causes fixed: the map list was fetched once at page init with no
creation event on the wire, and JS `changeMap` cleared characters/markers/roads that nothing re-added.
- **`mapUpdate` SSE is now an upsert** carrying the full `GET /map/api/maps` item shape
  (`{id, mapInfo:{name,hidden,priority,revision,defaultStartX/Y}, size}` camelCase — binds into
  `MapInfoModel` like the GET response). `isMainMap` is deliberately omitted (config lookup on the
  long-lived SSE scope would be stale); the client preserves its known value, new maps default false.
  Emitters: `GridService.ProcessGridUpdateAsync` new-map branch (sets `mapInfo.TenantId` first — the
  SSE loop filters by it), `HmapImportService.CreateNewMapAsync` (service now injects
  `IUpdateNotificationService`), `PUT /admin/maps/{id}/default-position` (was silent), plus the
  pre-existing rename/settings/public-import emitters. Client `HandleMapUpdated` upserts: unknown
  visible maps are added live (revision seeded via `SetMapRevisionAsync`/`InitializeMapRevision`),
  hidden updates remove + switch away (that branch was previously unreachable — the old flat payload
  `{id,name,hidden,priority}` only ever bound `ID`, which also blanked names on rename).
- **Merge lifecycle**: `MergeMapsAsync` takes the tenant from `ITenantContextAccessor` — the old
  per-merge tile lookup ran at post-shift coords and yielded `""` whenever the probed grid had no
  zoom-0 tile, so the SSE tenant filter silently dropped the merge event. After deleting the source
  map it now also broadcasts `NotifyMapDeleted(source)`, and the client's `HandleMapMerge` always
  removes `merge.From` from the selector (previously only reacted when viewing the source; dead maps
  stayed in the dropdown forever). When viewing the source, the camera switches to the target at
  center+shift (same world spot).
- **Dead camelCase SSE listeners fixed**: `mapDelete` had NEVER worked end-to-end (JS read
  `deleteInfo.Id` against the camelCase `{"id":…}` payload → `undefined` → swallowed), so admin
  deletes never reached viewers; same class fixed for `timerDeleted` and `OnCustomMarkerDeleted`
  (case-sensitive `GetProperty("Id")`). SSE loop also registers ids in the per-connection
  `tenantMapIds` on `mapUpdate` and `merge.From` — previously `mapDelete`/`mapRevision` for maps
  created after the connection opened were silently suppressed by the `Contains` gate.
- **Shared switch routine** in `Map.razor.cs`: `SwitchToMapAsync` + `ReloadMapScopedStateAsync` —
  every switch path (selector, merge, delete, became-hidden, follow-mode, center-on-character, panel
  refetch) now rebuilds game markers, re-adds characters (JS keeps no cross-map store; snapshot only
  arrives at SSE connect), **refetches** custom markers (`LoadCustomMarkersAsync` is mapId-scoped —
  the state service only ever holds one map's markers) and roads; JS `changeMap` now clears
  `PingManager` (pings aren't map-filtered in JS). `MapNavigationService` gained one canonical sort
  (IsMainMap desc, Priority desc, Name asc) used by `SetMaps`/`AddOrUpdateMap` — live updates used to
  drop the main-map-first ordering.
- **Belt-and-braces**: opening the Maps sidebar panel fires a background `GET /map/api/maps` refetch
  (`RefreshMapListAsync`) — covers any missed SSE window (the polling fallback it was also written for
  is gone since 2026-10-11); if the current map vanished meanwhile it behaves like a live deletion.
- **Races/init**: `MapView.ChangeMapAsync` queues `pendingMapId` when Leaflet hasn't fired `load` yet
  and applies it in `OnMapReady` (was a silent no-op → C#/JS desync); `/map?map=N` without x/y/z no
  longer snaps back to `Maps[0]` (respects `?map=` > MainMapId > first).
- **SPA re-entry killed the whole page** (the "black map until F5" bug): `leaflet-interop.js` is an
  ES module cached for the browser page's lifetime, so navigating dashboard → /map re-ran
  `initializeMap` with `mapHasInitialView` still true from the previous visit → `changeMap` skipped
  the initial `setView` → Leaflet `load` never fired on the new instance → `OnMapReady`/`initialized`
  never happened → every interop call (view, jump-to-player, markers, SSE init) silently no-oped:
  black map, dead clicks, URL stuck at `/map`. Fix: `initializeMap` now destroys the previous
  `mapInstance` (`.remove()`) and resets `mapHasInitialView`/`currentMapId`, and the document-level
  Alt+M keydown handler is replaced instead of stacking (each stale copy fired an extra ping).
  **Rule going forward: any module-level JS state consumed by `initializeMap`-style re-entry points
  must be reset there — Blazor SPA navigation does not reload JS modules.** (`map-updates.js` was
  re-entry-safe for SPA navigation only — not for circuit pause/resume or reconnect, where its old
  stream, timers and polling kept running against a dead `dotnetRef`. Since 2026-10-11
  `initializeSseUpdates` always opens a fresh stream and `disconnectSseUpdates` stops everything.)
- **Tests**: `GridServiceMapMergeTests` +2 — merge broadcasts with the real tenant (regression: the
  no-tile merge case that used to yield `""`) + `NotifyMapDeleted`, and the new-map branch notifies
  with `TenantId` set. No test host exists for the SSE endpoint/Blazor handlers.
- **Known gaps (flagged, not fixed here)**: polling-mode characters bound flat `X/Y` into nested
  `Position` (rendered at 0,0 — moot since the polling fallback was removed 2026-10-11); merge
  orphans map-scoped rows (CustomMarkers/Roads/OverlayData keep the deleted source MapId);
  `MapCleanupService` still disabled in Program.cs and tenant-blind.

### 2026-08-14: Cookbook bulk world assignment (tenant-admin)

**Untagged cookbook data can be bulk-assigned to a known world** — the cleanup for pre-world-tagging
catalogs (admin imports + old uploads) whose data all sits in the /cookbook "Untagged" bucket.
- **Endpoint:** `POST /api/tenants/{tenantId}/cookbook/assign-world` body `{"world":"<genus>"}` (same
  TenantAdmin policy + `CanManageTenant` guard as the other cookbook admin endpoints). Audited as
  `CookbookWorldAssigned` (counts + genus + display name), only when something changed. Idempotent —
  a re-run finds nothing untagged and returns `{Foods:0, Variants:0}` without an audit row.
- **Service:** `FoodCatalogService.AssignUntaggedToWorldAsync(tenantId, world)` — validates via
  `GameWorlds` (**known worlds only**, `OrderOf >= 0`; sentinel/blank/unknown → `ArgumentException` →
  400; ingestion stays permissive for unknown genus, this admin op does not). One transaction:
  keyset-pages untagged variants by Id (`Worlds.Count == 0` — EF9 translates primitive-collection
  Count to json_each SQL on SQLite; offset paging would skip rows as tagged ones leave the filter)
  in `VariantBatchSize` batches with `SaveChanges` + `ChangeTracker.Clear()` per batch, then one
  tracked food pass. Purely additive: appends genus to empty `Worlds` lists (foods also when a
  tagged food's untagged variant transferred — mirrors ingestion's food-level append) and **seeds
  `WorldValues` from the canonical columns** (`BuildWorldValueFromCanonical`) so a later real upload
  from that world competes under the lowest-FEP-total-wins heuristic instead of winning by default.
  Existing tags/snapshots never modified. Invalidates both tenant caches. No migration (JSON column
  values only). `GetStatusAsync`/`CookbookStatusDto` gained `UntaggedFoodCount`/`UntaggedVariantCount`.
- **UI:** Admin → Cookbook tab shows an "N foods / M recipe variations have no world tag" row
  (auto-hidden at 0) with a world dropdown (`GameWorlds.Known`, newest first) + "Assign world"
  button behind a counts-explicit `ShowMessageBox` confirmation ("cannot be undone — becomes
  indistinguishable from data uploaded from that world"). POST goes through the `APIUpload`
  HttpClient (the default `API` client's 10s resilience timeout would cancel/retry the bulk write).
  /cookbook needs no changes — after F5 the Untagged chip disables at 0 and world chips/values pick
  the data up (same accepted circuit staleness as import/clear).
  **MudBlazor gotcha:** `MudSelect` popovers are modal by default since v7 (`Modal="true"`), so with
  the dropdown open the first click on a nearby button only dismisses the invisible overlay — the
  world select sets `Modal="false"` so outside clicks pass through; do the same on future selects
  that sit next to action buttons.
- **Tests:** `FoodCatalogServiceWorldAssignTests` (real SQLite, same harness as export/import tests):
  tagging + snapshot seeding, merge into already-tagged foods without touching existing tags,
  no-op re-run, tenant isolation, validation, status counts, export roundtrip, and a
  `VariantBatchSize + 1` batch-boundary case proving the keyset loop.

### 2026-08-12: Cookbook export/import (tenant-admin)

**The cookbook can be exported as a re-importable JSON snapshot** — the piece a food-info2 re-import
can never restore is the player-contributed data (variants, world tags, TimesSeen), and this preserves it.
- **Format:** `CookbookExportDto` in `src/HnHMapperServer.Core/DTOs/CookbookExportDtos.cs` — object with
  `"format": "hnh-cookbook-export"` + `version` (currently 1), then `foods[]` each carrying all food fields
  (incl. `addedAt` = discovery date, worlds, categories/satiations, wiki fields) and nested `variants[]`
  (signature, TimesSeen, worlds, per-world values, feps, ingredients). **Contributors travel as usernames,
  not user ids** (portable per the data-files rule); import re-resolves them by `NormalizedUserName`,
  unknown names drop. `Format` is deliberately NOT defaulted to the marker in the DTO — detection
  deserializes arbitrary object files into it and a default would misdetect (e.g. wiki-food-data.json).
- **Endpoints:** `GET /api/tenants/{tenantId}/cookbook/export` (TenantAdmin policy + own-tenant guard,
  same as status/import/clear) returns the file download. Import is **the existing**
  `POST .../cookbook/import` — `FoodCatalogService.ImportAsync` sniffs the root: JSON array = game dump
  (legacy path unchanged), object = export snapshot (`TryReadExportSnapshot` → `ImportSnapshotAsync`,
  wipe-and-replace like the legacy path, wiki file ignored, signatures kept verbatim so panel/favorite
  pins survive, `ImportedAt` restored from `addedAt`). Newer `version` than the server knows → error, no wipe.
- **UI:** Admin → Cookbook tab gained an "Export cookbook" button (disabled while empty); the import
  file-picker accepts an export file through the same button (auto-detected). Download goes server-side
  via the `APIUpload` HttpClient (default `API` client would 10s-timeout) then to the browser through
  `DotNetStreamReference` + `window.downloadFileFromStream` (added to `wwwroot/js/file-upload-helper.js`).
  **Import stays wipe-and-replace by design** (restore/reseed semantics; merge would need conflict rules),
  but any import into a non-empty catalog now requires a confirmation dialog (`ConfirmReplaceAsync`)
  stating the exact food/variation counts being deleted — dump-flavored wording ("contributions cannot
  be restored, export first") vs snapshot-flavored ("restored as of that export") picked by filename
  heuristic (`cookbook-*.json`); the server still detects the format authoritatively. Empty catalog
  (day-one seed) imports without a prompt.
- **Tests:** `FoodCatalogServiceExportImportTests` (real SQLite — ExecuteDelete + unique indexes):
  roundtrip fidelity, tenant scoping, contributor username resolution/drop, wiki-like-object rejection
  without touching the catalog, newer-version rejection, legacy array path still working.

### 2026-08-12: Cookbook world (genus) filter

**Foods are tagged with the game worlds they were uploaded from, and /cookbook can filter by world.**
Both clients send a `genus` world hash per food record (Hurricane from `GameUI.genus`, KamiClient from
`ui.sess.user.genus`); the server used to drop it.
- **Registry:** `GameWorlds` in `src/HnHMapperServer.Core/Cookbook/GameWorlds.cs` — code-constant list of
  known worlds (`c646473983afec09` = W16, `b7c199a4557503a8` = W16.1, `fd63ddee958da329` = W16.2).
  **When the next world launches, add its genus hash + next Order there (one line) and deploy** — until
  then its uploads still tag/filter, shown as a shortened hash. `Normalize()` (trim, reject >64 chars),
  `DisplayName()`, `OrderOf()` (-1 unknown).
- **Data:** `Foods.Worlds` AND `FoodVariants.Worlds` — JSON string-list columns like `SatiationGroups`
  (migrations `AddFoodWorlds`, `AddVariantWorlds`), appended (deduped) in `IngestClientRecordsAsync`
  from `upload.Genus` on both the food and the exact variation (new + re-upload paths).
  `SourceFoodRecord` deliberately unchanged (import files have no genus). Empty list = untagged
  (admin imports + all pre-feature data; the user chose no backfill). Admin re-import wipes foods, so
  world tags reset and re-accumulate from uploads.
- **Per-world values:** `FoodVariants.WorldValues` (migration `AddVariantWorldValues`) — owned-JSON list
  `FoodVariantWorldValue {Genus, Energy, Hunger, Feps[]}` holding each world's representative snapshot
  (lowest FEP total within that world, the same closest-to-base heuristic; a higher re-upload never
  overwrites it). The plain variant columns stay the all-worlds merge. Food-level per-world values are
  NOT stored — `GetCatalogAsync` derives them (min-total across the food's variant snapshots) and ships
  them as `FoodDto.WorldValues`, plus `WorldVariantCounts`/`UntaggedVariantCount` (the catalog build now
  fetches variants whole instead of a GroupBy count — cached per tenant as before). With a world chip
  selected the UI shows world-effective values everywhere (master cells/pills/sort, FEP breakdown,
  prep-compare, variations table, threshold conditions via `TargetOf`): `RebuildRows()` re-derives every
  `FoodRow`/`VariantRow` from raw DTOs on world change (`SetWorld` is the only mutation path), with the
  canonical values as fallback where a world has no snapshot. `GET /api/v1/cookbook/filter-matches`
  gained `&world=` (genus or `untagged`, see `GameWorlds.UntaggedSentinel`) so the variant-aware match
  counts evaluate world-effective values and count only the selected bucket; the "N recipes" hint and
  the variations title show the bucket count (`WorldVariantCount`).
- **UI:** "World" chip row on /cookbook after Prep (chips = worlds present in the catalog, newest known
  first, then unknown hashes, + an "Untagged" chip). Single-select facet composing with search, FEP
  threshold conditions, satiation/prep/stat chips and panel filter via `Filtered`/`CountBase` (new
  `includeWorld` flag; world chip counts ignore their own row's selection like every facet). Selected
  world appears in the active-filters row and is cleared by Clear-all/ClearFilters. **Default selection =
  newest known world that has data** (`??=` in `LoadCatalogAsync`; null → no filter before any tagged
  uploads exist, so the page isn't empty on day one). Chip colors: `.stat-chip.world` #cfd9ec,
  `.untagged` #dfe3e6. All known-world chips render even at 0 foods (disabled + `.empty`; a selected
  chip stays enabled so it can be deselected). The variations sub-table follows the selected world with
  the same bucket semantics (`FilteredVariants`, skipped while a focus chip pins a variant), and tagged
  variant rows show a `world-tag` ("🌐 W16.1 +N", full hashes in the tooltip).
- **Deliberately NOT world-scoped:** variant identity (one row per `FoodId`+signature across worlds —
  per-world identity would break signature-keyed favorites/panels); panels/focus (name-keyed, bypass
  all filters — a panel chip can surface a food from another bucket); wiki-derived data (canonical,
  world-agnostic); the food's stored headline columns (creation-time wiki/first-upload values — the
  world view overrides them via the derived `WorldValues` instead).
- Tests: `GameWorldsTests` (Core). Ingestion/UI verified against the dev DB.

### 2026-08-14: Page-title glass pills (app.css)

The bare page headlines ("Admin Panel", "SuperAdmin Panel", the Cookbook title row) were the only text
sitting directly on the background artwork — dark `#2c3e50` heading text vanished over the image's dark
regions. New `.page-title` class in `wwwroot/app.css`: a glass pill (translucent white 0.65, same
border/shadow/radius as `.glass-morphism`, `inline-flex !important` so it hugs content and beats
MudStack's `display:flex`). Deliberately **no `backdrop-filter`** — body::before is already blurred and
nothing scrolls beneath these titles (see the white-flash rules below). Applied in `Admin.razor`,
`SuperAdmin.razor`, `Cookbook.razor` (title + icon + food count share the pill). Every other heading
already lives inside a glass paper; put any future bare-on-background headline in `.page-title`.

### 2026-08-11: Glassmorphism white-flash fix (app.css)

**White band flashing below glass containers on hover** (cookbook toolbar, panels bar, appbar — anywhere
`glass-morphism` sits) was a Chromium compositing artifact, not a DOM element: hover rules animate
`transform` (and one toggled `backdrop-filter`) on MudBlazor buttons inside `backdrop-filter` containers,
forcing the glass surface to re-rasterize; for those frames its blur-expanded margin composites against
the near-white canvas instead of the background image. Fix in `wwwroot/app.css`:
- `.glass-morphism` / `.glass-appbar` gained `transform: translateZ(0)` to pre-pin their compositor layer.
- `backdrop-filter` removed from all hover-transformed controls (filled/outlined/icon/fab base rules and
  the `.mud-button-text:hover` toggle). Their translucent white fills are unchanged — over the page's
  already-blurred `body::before` background the button-level blur was visually a no-op.
- **Rule going forward:** never combine `backdrop-filter` with a hover/transition `transform` on the same
  element, and don't nest `backdrop-filter` elements inside glass containers.

**Third mechanism — the intermittent "white bar at the toolbar's bottom edge" (2026-08-12):** Chromium's
`backdrop-filter` can only sample pixels inside the filtered element's own bounds, so the last
~blur-radius band at each edge is dominated by whatever single row of content sits at the boundary; as
content moves even 1px the band swings (crbug 40040614 / 41471914 — Chrome 129's "mirror" edge mode only
softened it, and residual flicker is machine/timing-dependent). Fix in app.css: `.glass-morphism` and
`.glass-appbar` no longer carry `backdrop-filter` themselves — the blur lives on an **oversized `::before`
(`inset: -60px`, `z-index: -1`) clipped by the element** (`position: relative; overflow: hidden`), which
pushes the blur's sampling edge 60px outside the visible clip where its artifacts can't be seen. Verified
frame-by-frame via CDP screencast during 1px-step scrolling (bottom-band luma spread ≤2/255, no layout
regressions on cookbook/dashboard/login). **Do not put `backdrop-filter` back on glass elements directly,
and don't add `overflow: visible` children that must poke outside a glass container** (the clip is
load-bearing). The pseudo needs the element's stacking context (`transform: translateZ(0)`) to stay
behind content. Follow-up (same day): the residual "darker flickering" over the pinned stack came from
the last two in-bounds blurs on the page — `.panel-card` (blur band spans nearly the whole condensed
chip floating over scrolling rows) and the `ck-stuck` slit pseudo. `.panel-card` now uses the same
oversized-`::before` pattern; the slit cover is a plain `rgba(255,255,255,0.7)` tint (blur is
imperceptible on a 7px strip).

**Second mechanism, pixel-verified via puppeteer screenshots:** the cookbook's pinned `.sticky-stack` had
two fully transparent strips — the slit between the appbar (~65px) and the stack (top: 72px), and the
toolbar's 12px bottom margin — through which raw table-row slices showed while scrolling under the stack;
that content strip was "the bar below the banner". Fix in `Cookbook.razor.css` (`.ck-stuck` state only, so
the resting layout is untouched): the toolbar swaps its bottom margin for equal padding so its own glass
paints the gap (flush with the panels bar, squared meeting corners), and a `::before` on the stack frosts
the appbar slit. That pseudo must live on the stack itself — putting it on a glass child would trap its
`backdrop-filter` inside the child's backdrop root and the blur would sample nothing.

### 2026-08-11: Cookbook FEP palette — Cediner two-tone

**FEP stat colors on /cookbook now match Cediner's hnh-food-book** (palette from its `FEPBar.vue`):
- `StatColors` in `Cookbook.razor` maps each stat to a `(Tier1, Tier2)` pair — muted tier-1, brighter
  tier-2; `StatColor(attr, tier = 1)` is the single lookup (unknown keys still fall back to `#d8d8d8`).
- Tier-aware sites (table/detail/prep-compare FEP pills, both bar types, panels-strip mini bars,
  dist dots, hover-tooltip dot) pass `fep.Tier`; tier-agnostic chips (FEP row, builder chips, tierless
  threshold chips) use tier-1, while tier-specific threshold chips (`int2>15`) get the tier-2 hue.
- The gold ring / diagonal-shine tier-2 marker was removed from `Cookbook.razor.css` (`.fep-pill.tier2`,
  `.item-bar-seg.tier2`, the shimmer block); the "+2" text labels remain the non-color tier signal.

### 2026-08-11: Cookbook FEP threshold filters

**Cediner-style threshold filtering on /cookbook** — expressions typed straight into the search box,
mixed with free text (`meat str>50% int2>15`):
- **Syntax:** `key[tier]op value[%]`. Keys: the 9 stats (`str agi int con per cha dex will psy`;
  bare = tier1+tier2 combined, `int2`/`str1` = tier-specific) plus `total`, `hunger`, `energy`,
  `eff` (FEP/hunger). Ops `> >= < <= =` (`==` alias). `%` (stat keys only) = share of the food's
  total FEP, quality-invariant; absolute stat/`total`/`eff` values compare **quality-scaled** (WYSIWYG
  with the table at the current Q input, rounded to 2 dp); `hunger`/`energy` unscaled. Conditions AND
  together and with the residual text search.
- **Parser:** `FepFilterParser` in `src/HnHMapperServer.Core/Cookbook/FepFilterParser.cs` — pure
  regex extraction returning (conditions, residual text). Invalid tokens (`hunger>50%`, typos,
  `straw`) stay in the residual text and degrade to a normal 0-result search; with zero conditions
  the input passes through byte-identical. Unit-tested in `FepFilterParserTests` (Core is referenced
  by the test project; Web is not — that's why the parser lives in Core).
- **UI (cediner-inspired, modeled on github.com/Cediner/hnh-food-book):** filtering has one home,
  a "Filter" row directly under the Prep chips — the expression input (`FilterText`; unparsed
  remainder surfaces as an "Ignored: …" helper line, never an error) with a key-chip helper row
  beneath (9 stat chips + Total/Hunger/Energy/FEP-H from `FilterKeyHelpers`): clicking a key chip appends `key>=` and focuses the input with the
  caret at the end (`AppendKeyToFilterAsync` — the pending render flushes at its first await, so
  the text lands before focus and MudBlazor's focused-text suppression never bites). Quarter
  chips (≥25/50/75/100%, `QuarterPresets`) are context-aware: they complete a started stat
  expression at the end of the input (`TrailingStatOpRegex`, e.g. `str>=` → `str>=50%`), else
  apply to the selected FEP chip via `ApplyToolExpression(..., toggle: true)` (re-click removes,
  different quarter replaces), else render `.empty` with an explanatory tooltip. Conditions
  live as text in two inputs — that filter input plus the search box, which still parses mixed
  syntax as a power path — each parsed once per change in its property setter
  (`Search`/`FilterText`) with `_allConditions` as the union every consumer reads. Active chips:
  body click sorts by exactly what the chip filters (`SortKeyOf` + shared `ConditionValue`, ▼/▲
  indicator, self-clearing when the chip disappears), the nested ✕ button removes; the row also
  shows the selected FEP/satiation/prep facet as removable chips (colored like their source rows,
  whole chip = clear), and the clear-all chip (shown when several chips are active,
  `ActiveFilterChipCount`) drops facets + conditions while keeping the text search. A "⊞ Columns" chip at the end of the FEP toggle row expands one
  sortable, display-only column per stat (quality-scaled `.stat-value` pills; the FEP-pills and
  selected-stat columns hide meanwhile — every colspan derives from `VisibleColumnCount`). The
  in-table header filter boxes were tried and **removed as too complex a UX**; tier-specific
  conditions (`int2>15`) stay syntax/chip-only. There is no builder UI. Chip counts are
  facet-aware via `CountBase(includeSatiation, includePrep, includeStat)`: each row's counts
  reflect search + conditions + panel + the OTHER facet rows' selections, excluding the row's
  own (so sibling chips show what selecting them would yield instead of zeroing out).
- **One-click tools** (all funnel through `ApplyToolExpression`, which replaces same-shaped
  conditions — same key/tier/%-ness — wherever they live and appends the new expression to the
  filter input; multi-condition expressions apply/toggle as one unit): master-row FEP pills
  (`str2>=8`, tier always explicit; their hover card gains a `data-ft-hint` line via
  `FtAttrs(..., clickable: true)` + `fep-tooltip.js`) and Total / FEP-Hunger cell values
  (`total>=…` / `eff>=…`, "—" cells inert). The Efficient/Light/Feast preset chips were tried
  and removed as not useful. Tool values are rounded to 2 dp and invariant-formatted (display
  strings are culture-formatted — never reuse them), so a clicked row always passes its own
  `>=` filter.
- **Variant-aware matching:** condition evaluation lives in Core
  (`FepConditionEvaluator`/`FepConditionTarget` in `src/HnHMapperServer.Core/Cookbook/`, unit
  tested) and is shared by the client and `GET /api/v1/cookbook/filter-matches?expression=&quality=`.
  The endpoint returns `FoodConditionMatchDto`s (food id, whether the base passes, how many
  variations pass) for foods whose **base OR any recipe variation** passes all conditions
  (so Nut Jerky shows for `str>50%` when only some variations exceed 50%), evaluated over a
  per-tenant cached compact structure (`cookbook:conditionstats:{tenantId}`, invalidated wherever
  the catalog cache is). The client fires a cancellable fetch per conditions/quality change
  (`QueueServerMatchRefresh`), filters master rows by the result, and shows per-food matching
  counts in the row hint ("12 of 4992 recipes"); base-only evaluation is the fallback while
  pending/on error, and the expanded variations sub-table shows exactly the passing variations.
- **Variations:** conditions also filter the per-food variations sub-table via the shared
  `ConditionTarget` evaluator (`VariantRow` carries `StatTotals`/`StatTierTotals`; percent = share
  of the variant's own total; skipped while a focused panel chip pins a variant).
- **Data:** `FoodRow` gained `StatTierTotals` (keys `"STR1"`/`"STR2"`) since the existing
  `StatTotals` merges tiers.
- **Variant contributors (2026-08-12):** `FoodVariants.Contributors` — a JSON string-list column
  (like `SatiationGroups`, migration `AddVariantContributors`, default `[]`) holding the Identity
  UserIds of *everyone* whose client upload reported that exact variation: set on new-variant
  ingestion, appended (deduped) when a re-upload bumps `TimesSeen`. `FoodVariantDto.ContributorNames`
  resolves usernames batched in `GetVariationsAsync`; the variations table shows a `contrib-name`
  tag ("name +N", full list in the tooltip). Pre-existing variations stay anonymous (`[]`) — the
  uploader was never recorded. Food-level `ContributedBy` (first discoverer) is unchanged.

### 2026-08-10: Superadmin tenant data purge

**Reclaim disk from a tenant without losing the tenant:**
- **Endpoint:** `POST /api/superadmin/tenants/{tenantId}/purge-data` (SuperadminOnly). Body must echo
  `{ "confirmTenantId": "<tenantId>" }` — a deliberate speed bump. Returns a `PurgeTenantDataResultDto`
  with per-table counts, `filesDeleted`, `bytesFreed`/`megabytesFreed`, `deletedMapIds` and any warnings.
- **Deleted:** Maps, Grids, Tiles, Markers, CustomMarkers, Roads, Pings, OverlayData, OverlayOffsets,
  DirtyZoomTiles, Timers (+ cascaded TimerWarnings), TimerHistory, Notifications,
  the `mainMapId` config key, and `PublicMapSources`/`PublicMapSourceAlignments` that pointed at the
  wiped maps. On disk: all of `{GridStorage}/tenants/{tenantId}` and `{GridStorage}/previews/{tenantId}`.
- **Kept:** the tenant row, TenantUsers, TenantPermissions, TenantInvitations, Tokens, remaining Config,
  AuditLogs, Identity users, and **the whole cookbook** — Foods/FoodVariants plus FoodPanels/favorites
  (changed 2026-08-14: foods hold player contributions no re-import can restore, and they cost no tile
  storage; use the tenant-admin cookbook clear if the catalog itself must go).
- **Service:** `ITenantDataPurgeService` / `TenantDataPurgeService`. Every query is `IgnoreQueryFilters()`
  plus an explicit `TenantId` predicate, since the superadmin request's ambient tenant is a different one.
  Deletes run child-before-parent inside one transaction. Files are removed after
  the commit, then `RecalculateStorageUsageAsync` resets `CurrentStorageMB` and the directory skeleton is
  recreated so clients can upload immediately.
- **UI:** red "delete sweep" icon in SuperAdmin → Tenants row actions → `PurgeTenantDataDialog`, which
  loads `/statistics` to show exact deleted/kept counts and requires typing the tenant id (its name is
  accepted too). It posts via the `APIUpload` HttpClient — the default `API` client's resilience handler
  would time out and retry at 10s. Row-action clicks now stop propagation so they no longer also trigger
  row navigation.
- **MudBlazor gotcha:** for per-keystroke text fields use `Immediate="true"` only. Adding
  `@bind-Value:event="oninput"` on a *component* compiles fine but emits the callback as a parameter
  literally named `oninput`, so `ValueChanged` never binds and the field silently stays empty.
- **Note:** SQLite does not shrink `grids.db` on delete (no VACUUM); the reclaimed space is the tile/grid
  image tree, and freed DB pages are reused by later writes.
- **Tests:** `TenantDataPurgeServiceTests` (10 tests) run against real SQLite — the in-memory provider
  cannot execute `ExecuteDelete`, and only SQLite enforces the FK ordering the service depends on.

### 2026-07-19: Cookbook (cookbook-v2 branch)

**Per-tenant food catalog with community contributions:**
- **Data:** `Foods` + `FoodVariants` (tenant-scoped, EF9 JSON columns for FEPs/ingredients/groups; ~928 foods / ~49k recipe variations per tenant) and `FoodPanels` + `FoodPanelItems` (per-user collections, name-keyed items survive re-imports). Migrations: `AddCookbook`, `AddFoodPanelsAndContributors`, `AddCanonicalRecipe`.
- **Game-client uploads:** `POST /client/{token}/food` (Upload permission) accepts Hurricane "Cookbook Integration" (JSON array; endpoint = `{server}/client/{token}/food`, its token field stays empty) and KamiClient autofood (JSON object; mapper endpoint + autofood toggle). Additive ingestion with wiki enrichment, contributor attribution (shown in UI), and a tenant-wide notification digest for new foods. Hurricane q10-normalizes before sending.
- **Wiki data:** bundled dump `src/HnHMapperServer.Api/Data/wiki-food-data.json` (1036 ringofbrodgar pages incl. scraped intermediates) ships inside the Docker image; supplies canonical base-q10 values, satiation groups, canonical recipes (`RecipeText`/`CookingStation` parsed from `objectsreq`/`producedby`). Rescrape tool lives outside the repo (`../tools/scrape_wiki.py`).
- **UI `/cookbook`:** searchable/sortable catalog (name or ingredient), FEP/satiation/preparation filter chip rows, quality input scaling FEPs by √(q/10), row expansion with recipe trees (recursive sub-ingredients via `GET /api/v1/cookbook/recipe-index`, prep-variants inherit base recipes), per-recipe variation tables, panels strip (drag & drop + click-to-add, Favorites star, sharing with per-owner titles; scrolls with the page — the pinned/condensing sticky stack was removed 2026-08-15), contrast-checked `--ck-*` text tiers.
- **Panels API:** 8 endpoints under `/api/v1/cookbook/panels` (CRUD, items, reorder, favorites toggle).
- **Tenant-admin import/clear:** `GET/POST/DELETE /api/tenants/{tenantId}/cookbook[/status|/import]` — TenantAdmin policy + in-handler own-tenant guard, audited (`CookbookImported`/`CookbookCleared`); Admin panel → Cookbook tab (no tenant selection), clear-all behind a counts-explicit confirmation.
- **Token lists** (Admin → Tokens and the dashboard) show both endpoint URLs per token — Mapper and Cookbook — with copy buttons and client setup instructions.

### 2025-11-15: Multi-Tenancy Implementation (tenancy branch)

**Complete multi-tenancy system implemented:**
- ASP.NET Core Identity migration (AspNetUsers, AspNetRoles)
- 5 new tables: Tenants, TenantUsers, TenantPermissions, TenantInvitations, AuditLogs
- All existing tables tenant-scoped with TenantId column
- 7+ database migrations applied
- Tenant-prefixed tokens (`{tenantId}_{secret}`)
- EF Core global query filters for automatic tenant isolation
- Tenant-isolated file storage (`map/tenants/{tenantId}/grids/`)

**New Endpoints:**
- TenantAdminEndpoints: 10 endpoints for tenant management
- SuperadminEndpoints: 13 endpoints for global management
- InvitationEndpoints: 4 endpoints for invitation workflow
- AuditEndpoints: Audit log access

**New UI:**
- SuperAdmin.razor: Superadmin dashboard
- TenantDetails.razor: Tenant details page
- PendingApproval.razor: User approval workflow
- PendingAssignment.razor: Superadmin assignment workflow
- TenantList, UnassignedUsersList, AssignUserDialog components

**New Services:**
- TenantNameService: Generates readable tenant IDs
- TenantContextAccessor: Resolves tenant from token/claims
- StorageQuotaService: Storage quota management
- AuditService: Audit logging
- InvitationExpirationService: Auto-expires invitations
- TenantStorageVerificationService: Verifies quotas

**Authorization:**
- SuperadminOnlyHandler, TenantAdminHandler, TenantPermissionHandler
- TenantClaimsPrincipalFactory: Injects tenant claims
- TenantContextMiddleware: Resolves tenant context

### 2025-11-06: Custom Markers, SSE, Deployment & Security

- Custom markers with CRUD API (5 endpoints)
- SSE character streaming (replaced HTTP polling)
- Docker deployment with CI/CD pipeline
- Security hardening (CORS disabled, HTTPS opt-in)
- Production configuration files

---

## Future Enhancements

### Priority 1
- [ ] Map management UI (edit properties, bulk operations)
- [ ] Export/Import functionality (ZIP-based migration)
- [ ] Rebuild zoom tiles implementation
- [ ] Rate limiting on login/registration endpoints

### Priority 2
- [ ] Two-factor authentication (2FA)
- [ ] Email notifications (invitations, quota warnings)
- [ ] Performance metrics dashboard
- [ ] Advanced search/filtering in admin lists

### Priority 3
- [ ] Tenant tiers & billing (Free, Pro, Enterprise)
- [ ] Custom domains per tenant
- [ ] Multi-language support
- [ ] Dark mode
- [ ] API documentation (Swagger/OpenAPI)

---

## Resources

### Documentation
- [MULTI_TENANCY_DESIGN.md](MULTI_TENANCY_DESIGN.md) - Complete multi-tenancy architecture (7,043 lines)
- [API_SPECIFICATION.md](API_SPECIFICATION.md) - All endpoints with schemas
- [DATABASE_SCHEMA.md](DATABASE_SCHEMA.md) - Complete schema with migrations
- [deploy/VPS-SETUP.md](deploy/VPS-SETUP.md) - Deployment guide
- [deploy/SECURITY.md](deploy/SECURITY.md) - Security best practices
- [DEPLOYMENT.md](DEPLOYMENT.md) - Deployment architecture

### External Links
- [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [MudBlazor](https://mudblazor.com/components/list)
- [EF Core](https://learn.microsoft.com/en-us/ef/core/)
- [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/)

---

## Contributing

When making changes:
1. Maintain backward compatibility with game clients
2. Update this CLAUDE.md to reflect changes
3. Test multi-tenant isolation (verify tenant data doesn't leak)
4. Test authentication across Web and API services
5. Follow existing patterns (Minimal APIs, Clean Architecture)
6. Add audit logging for sensitive operations

---

**This documentation is for AI assistants to understand the project structure, current implementation status, and key technical decisions.**
