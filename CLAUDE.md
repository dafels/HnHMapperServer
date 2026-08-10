# HnH Mapper Server - Project Documentation for AI Assistants

**Last Updated:** 2026-08-10
**Project Status:** Production-Ready (Core + Admin + Multi-Tenancy + Cookbook)
**Tech Stack:** .NET 9, ASP.NET Core, Blazor Server, MudBlazor, SQLite, .NET Aspire, Docker
**Current Branch:** `master`

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
| **UI** | MudBlazor 6.11.2 |
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
- **Invitation System**: Invite-code based registration with admin approval workflow
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
- **Invitation** (4 endpoints): Create, validate, list, revoke invitations

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
- 250ms server-side coalescing with bounded channels (capacity 1024)
- Events: `charactersSnapshot`, `characterDelta`, `customMarkerCreated/Updated/Deleted`, `mapDelete`

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

---

## Authentication & Authorization

### ASP.NET Core Identity

**Migration from custom auth completed:**
- Uses ASP.NET Identity with AspNetUsers, AspNetRoles tables
- Password hashing via Identity (PBKDF2)
- Cookie-based authentication with Data Protection API
- Shared keys in `map/DataProtection-Keys/` for Web/API cookie sharing

**Multi-Tenant Authentication Flow:**
1. User logs in at `/login`
2. If user belongs to multiple tenants → tenant selection page
3. Cookie created with tenant context in claims
4. `TenantClaimsPrincipalFactory` injects tenant-specific claims
5. `TenantContextMiddleware` resolves tenant from token or claims
6. All database queries automatically filtered by tenant via EF Core global query filters

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

### 2026-08-10: Superadmin tenant data purge

**Reclaim disk from a tenant without losing the tenant:**
- **Endpoint:** `POST /api/superadmin/tenants/{tenantId}/purge-data` (SuperadminOnly). Body must echo
  `{ "confirmTenantId": "<tenantId>" }` — a deliberate speed bump. Returns a `PurgeTenantDataResultDto`
  with per-table counts, `filesDeleted`, `bytesFreed`/`megabytesFreed`, `deletedMapIds` and any warnings.
- **Deleted:** Maps, Grids, Tiles, Markers, CustomMarkers, Roads, Pings, OverlayData, OverlayOffsets,
  DirtyZoomTiles, Timers (+ cascaded TimerWarnings), TimerHistory, Notifications, Foods/FoodVariants,
  the `mainMapId` config key, and `PublicMapSources`/`PublicMapSourceAlignments` that pointed at the
  wiped maps. On disk: all of `{GridStorage}/tenants/{tenantId}` and `{GridStorage}/previews/{tenantId}`.
- **Kept:** the tenant row, TenantUsers, TenantPermissions, TenantInvitations, Tokens, remaining Config,
  AuditLogs, Identity users, and cookbook FoodPanels/favorites (name-keyed, so they re-resolve after a
  future import — same rationale as the existing cookbook clear).
- **Service:** `ITenantDataPurgeService` / `TenantDataPurgeService`. Every query is `IgnoreQueryFilters()`
  plus an explicit `TenantId` predicate, since the superadmin request's ambient tenant is a different one.
  Deletes run child-before-parent inside one transaction; the cookbook clear runs first because
  `FoodCatalogService.ClearAsync` opens its own transaction on the same DbContext. Files are removed after
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
- **UI `/cookbook`:** searchable/sortable catalog (name or ingredient), FEP/satiation/preparation filter chip rows, quality input scaling FEPs by √(q/10), row expansion with recipe trees (recursive sub-ingredients via `GET /api/v1/cookbook/recipe-index`, prep-variants inherit base recipes), per-recipe variation tables, panels strip (drag & drop + click-to-add, Favorites star, sharing with per-owner titles, condense-to-headers when pinned over the table), contrast-checked `--ck-*` text tiers.
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
