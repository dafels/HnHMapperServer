# HnH Mapper Server - Project Documentation for AI Assistants

**Last Updated:** 2026-08-23
**Project Status:** Production-Ready (Core + Admin + Multi-Tenancy + Cookbook)
**Tech Stack:** .NET 10 (LTS), ASP.NET Core, Blazor Server, MudBlazor 9, SQLite, .NET Aspire 13, Docker
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
  `mapUpdate` (**upsert** — full map item incl. new maps), `mapDelete`, `mapRevision`,
  `customMarker*`, `marker*`, `ping*`, `road*`, `timer*`, `notification*`, `overlayUpdated`

**Map APIs:**
- `GET /map/api/v1/characters` - Character list (deprecated, use SSE)
- `GET /map/api/v1/markers` - Marker list
- `GET /map/grids/{mapid}/{zoom}/{x}_{y}.png` - Tile images (6 zoom levels)
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
| `TenantStorageVerificationService` | 6 hours | Verify storage quotas |
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

- `GridStorage`: Data directory (default: "map")
- `Cleanup:DeleteEmptyMapsAfterMinutes`: Empty map retention (default: 60)
- `Cleanup:MapCleanupIntervalSeconds`: Cleanup interval (default: 600)

### Runtime Configuration

Stored in `Config` table (tenant-scoped):
- `title`: Site title
- `prefix`: URL prefix for token display
- `defaultHide`: Default hidden status for new maps

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

**Real-time tracking:**
- Atomic updates on tile upload/delete
- Background verification every 6 hours
- Upload rejection when quota exceeded (413 status)
- UI gauge showing usage percentage

### Tenant Isolation

**EF Core Global Query Filters:**
```csharp
modelBuilder.Entity<Map>().HasQueryFilter(m => m.TenantId == _tenantContext.TenantId);
```
- All queries automatically filtered by tenant
- No manual TenantId checks required in business logic
- Prevents cross-tenant data leakage

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
**Solution:** Verify `AuthenticationDelegatingHandler` registered and Data Protection keys shared

### "Build fails with file locked errors"

**Cause:** Running services lock DLL files
**Solution:** `taskkill /F /IM dotnet.exe`

### "User authenticated but has no roles"

**Cause:** TenantUser not approved or permissions not set
**Solution:** Admin must approve user and assign permissions in admin panel

---

## Recent Changes

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
- **Known follow-ups (not done here):** ~89 dead MudBlazor attributes flagged by MUD0002 since the
  v8 days (`PreventDuplicates`/`MaxDisplayedSnackbars`/`VisibleStateDuration`/`PositionClass` on
  MudSnackbarProvider, `Clickable` on MudList, `Filter`/`SelectedChip` on MudChipSet, `Option` on
  MudRadio, `DisablePortal`/`OpenMenuOnFocus` on MudAutocomplete, `Title` on MudIconButton/MudFab,
  …) — they compile but do nothing, so that UI configuration has been silently inert; and one
  malformed icon URL `/f:gfx/invobjs/leaf-brassica.png` (an `f:`-prefixed path leaking into
  `FoodIcons`). Optional .NET 10 adoption work: named query filters for the 17 tenant filters,
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
  `AuthenticationDelegatingHandler` forwards `X-Forwarded-For`/`-Proto` (cached in
  `AuthenticationStateCache.ClientOrigin` for circuit threads) — without it every web user shared the web
  container's bucket and audit IP.
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
- **Durable backstop:** the 5-min dirty scan (`LargeTileGenerationService`) now also derives WebP
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
  heals them). Per-zoom newest-data aggregates are built by halving cell dictionaries upward; ~1
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
the whole viewport after every `mapRevision` bump because `SmartTileLayer.setMapRevision` clears
the map's entire negative cache before refreshing.

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
  havenandhearth.com/mt/r fallback, same as the cookbook table; the bell uses `RemoteFallbackOrHide`,
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
  (`RefreshMapListAsync`) — covers the polling fallback (`/map/api/v1/poll` carries no map list) and
  any missed SSE window; if the current map vanished meanwhile it behaves like a live deletion.
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
  already re-entry-safe: it re-binds `dotnetRef` and Map.razor's `DisposeAsync` closes the
  EventSource, so re-entry reconnects and gets a fresh snapshot.)
- **Tests**: `GridServiceMapMergeTests` +2 — merge broadcasts with the real tenant (regression: the
  no-tile merge case that used to yield `""`) + `NotifyMapDeleted`, and the new-map branch notifies
  with `TenantId` set. No test host exists for the SSE endpoint/Blazor handlers.
- **Known gaps (flagged, not fixed here)**: polling-mode characters bind flat `X/Y` into nested
  `Position` (render at 0,0); merge orphans map-scoped rows (CustomMarkers/Roads/OverlayData keep the
  deleted source MapId); `MapCleanupService` still disabled in Program.cs and tenant-blind.

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
