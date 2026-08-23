# HnH Mapper Production Deployment Files (Multi-Tenant)

This directory contains all the files needed to deploy HnH Mapper to a production Linux VPS using Docker with **full multi-tenancy support**.

**Multi-Tenancy Features:**
- Invitation-based user registration (self-registration disabled in production)
- Per-tenant data isolation (database and file system)
- Storage quotas per tenant
- Audit logging for all sensitive operations
- Granular role-based permissions

## Files

- **`docker-compose.yml`** - Docker Compose stack definition with API, Web, Caddy, and Watchtower services
- **`Caddyfile`** - Caddy reverse proxy configuration for path-based routing with security headers
- **`VPS-SETUP.md`** - Comprehensive step-by-step guide for VPS setup and deployment
- **`FIRST-TIME-SETUP.md`** - Detailed first-time deployment guide with multi-tenancy setup
- **`SECURITY.md`** - Production security configuration and hardening guide with multi-tenant security model

## Quick Start

### 1. Prerequisites

- Linux VPS (Ubuntu/Debian recommended)
- Docker and Docker Compose installed
- GitHub Container Registry images published (via GitHub Actions)

### 2. Initial Setup

```bash
# On VPS: Create directories
sudo mkdir -p /srv/hnh-map /opt/hnhmap
sudo chown $USER:$USER /srv/hnh-map /opt/hnhmap

# Copy files to VPS
scp docker-compose.yml Caddyfile USER@VPS_IP:/opt/hnhmap/

# On VPS: Edit docker-compose.yml
cd /opt/hnhmap
nano docker-compose.yml
# Replace 'OWNER' with your GitHub username
```

### 3. Deploy

```bash
# Pull and start services
docker compose pull
docker compose up -d

# Open firewall
sudo ufw allow 80/tcp

# Check logs
docker compose logs -f
```

### 4. Access and First-Time Setup

Navigate to `http://YOUR_VPS_IP` in your browser.

**Default login:**
- Username: `admin`
- Password: `admin123!`

**⚠️ Change the password immediately after first login!**

**Multi-Tenant First-Time Setup:**
1. **Change admin password** (Admin → Account → Change Password)
2. **Create invitation codes** for new users (Admin → Invitations → Create Invitation)
3. **Share invitation codes** with users who need access
4. **Approve new users** after they register (Admin → Pending Users → Approve)
5. **Assign permissions** to users (Map, Markers, Pointer, Upload, Writer)
6. **Generate tokens** for game client users (Admin → Tokens → Create Token)

**For detailed first-time setup:** See [FIRST-TIME-SETUP.md](FIRST-TIME-SETUP.md)

## Architecture

```
Internet (HTTP :80)
    ↓
Caddy Reverse Proxy
    ├── /client/* → API Service (game client endpoints)
    ├── /map/api/* → API Service (map viewer API)
    ├── /map/updates → API Service (SSE real-time updates)
    ├── /map/grids/* → API Service (tile images)
    ├── /admin/* → API Service (admin API endpoints)
    ├── /admin → Web Service (admin panel UI)
    └── /* → Web Service (default, login, dashboard)
```

Both API and Web services share `/srv/hnh-map` for:
- SQLite database with multi-tenant tables (`grids.db`)
- Tenant-isolated tile storage (`tenants/{tenantId}/grids/`)
- Cookie encryption keys (`DataProtection-Keys/`)

**Multi-Tenant Storage Structure:**
```
/srv/hnh-map/
├── grids.db                    # SQLite database with multi-tenant tables
├── DataProtection-Keys/        # Cookie encryption keys
└── tenants/                    # Tenant-isolated file storage
    ├── default-tenant-1/grids/ # Default tenant tiles
    └── {other-tenants}/grids/  # Additional tenant tiles
```

## Services

| Service | Container Name | Purpose | Exposed Port |
|---------|---------------|---------|--------------|
| **api** | hnhm-api | Game client APIs, map endpoints, admin APIs | Internal only |
| **web** | hnhm-web | Blazor UI (login, dashboard, admin panel) | Internal only |
| **caddy** | hnhm-caddy | Reverse proxy with path-based routing | 80 (HTTP) |
| **watchtower** | hnhm-watchtower | Automatic container updates from GHCR | N/A |

## Multi-Tenancy

**The application is fully multi-tenant** with complete data isolation between tenants.

### Default Tenant

On first deployment:
- Default tenant `default-tenant-1` is automatically created
- Bootstrap admin assigned to this tenant with TenantAdmin role
- All permissions granted (Map, Markers, Pointer, Upload, Writer)

### User Registration and Self-Service Onboarding (2026-08-23)

Onboarding is self-service — no admin approval queue. **All of it is configured in the web UI:
SuperAdmin → Sign-in** (toggles, credentials and step-by-step setup guides; changes apply instantly, no
restart, no compose edits). Stored in the database with secrets encrypted by the shared DataProtection keys.

- **Create a map:** any signed-in player can create a map (tenant) from the welcome screen or the map menu
  and becomes its admin. Toggle, server-decided quota (never client-supplied) and per-player cap live in
  SuperAdmin → Sign-in; plus an IP rate limit of 3 creations/hour.
- **Join a map:** a map admin shares an invite link (`/invite/{code}`). Anyone with the link joins
  **immediately** with the link's access preset — the link is the approval. Links are multi-use, expire
  (7/30/90 days) and can be capped (uses) and revoked. Presets: *Full access* (all five permissions,
  including Writer) or *View + upload* (no Writer).
- **Accounts:** "Open registration" (SuperAdmin → Sign-in) controls whether someone WITHOUT an invite link
  can create a password account. A valid invite link always permits registration. **Steam** and **Discord**
  sign-in are switched on there too (Steam needs nothing but an optional free Web API key; Discord needs a
  Discord application with the redirect URL the page shows you); first sign-in creates the account, and an
  invite link carried through the sign-in joins the map in the same step. The compose files carry no
  provider settings — `SelfRegistration__Enabled` on the api service only seeds the very first start.

#### Security notes for self-service onboarding

- **Invite links are bearer credentials.** Whoever holds one joins with the preset's permissions — with
  *Full access* that includes deleting tiles and markers. Use *View + upload* and a use cap for links shared
  in large channels; revoke links you no longer need (Map admin → Invite links). Every redemption is an
  audit row (`InvitationRedeemed`) and every member list shows how each member got in.
- **Rate limits** (per client IP; the web service forwards the browser's address to the API):
  register 5/h, login 20/min, map creation 3/h, invite redeem 10/min, invite validate 30/min.
- **XFF trust:** both services trust `X-Forwarded-For` from any source (`KnownProxies` cleared). That is
  only safe because Caddy rewrites the header for external traffic and the API port 8080 is never
  published. Do not expose the API container directly.
- **TLS:** cookies are issued with `SecurePolicy=SameAsRequest`. On plain-HTTP deployments sessions and
  invite-link clicks travel in cleartext — with open registration and external providers, run on a domain
  with Caddy TLS. Discord additionally requires the exact HTTPS redirect URI `https://{domain}/signin-discord`
  to be registered in its developer portal; Steam works on HTTP too but shows the bare IP as the realm.
- **Secrets:** the Steam key and Discord client secret are entered once in SuperAdmin → Sign-in and stored
  encrypted (DataProtection, same key ring both services share - keep `/data/DataProtection-Keys` in your
  backups or the secrets must be re-entered). They are never shown again in the UI and never logged.
  Provider tokens are never persisted; only the provider's user id is stored (AspNetUserLogins). Switching a
  provider off removes its sign-in route immediately.
- **Overview:** SuperAdmin → Accounts lists every account with its sign-in methods, registration source,
  last login and memberships (joined via invite link / created the map / added by admin).

Legacy note: the old single-use invitation codes and the "Pending Users" approval queue still work for
rows created before this change; new links never produce pending users.

### Roles and Permissions

**Roles:**
- **TenantAdmin:** Manage users, tokens, and invitations within their tenant
- **TenantUser:** Standard user with granular permissions

**Permissions:**
- **Map:** View maps
- **Markers:** View and create markers
- **Pointer:** View character positions
- **Upload:** Upload tiles via game client (required for game client users)
- **Writer:** Edit/delete tiles and markers (admin-level permission)

### Token Format

Game client tokens include the tenant ID prefix:
```
Format: {tenantId}_{secret}
Example: default-tenant-1_a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6
```

Tokens are generated in the Admin panel → Tokens tab.

### Storage Quotas

Each tenant has a storage quota (default: 1024 MB):
- Real-time tracking of tile storage usage
- Upload rejected when quota exceeded (HTTP 413)
- Quotas adjustable in Admin panel → Config tab

### Audit Logging

All sensitive operations are logged:
- User creation, deletion, role changes
- Permission grants/revokes
- Token creation/revocation
- Invitation creation/usage
- Admin panel actions

**Access audit logs:** Admin panel → Audit Logs tab (tenant-scoped)

## Auto-Updates

Watchtower monitors GHCR for new `:main` tagged images and automatically updates containers every 60 seconds.

**To trigger an update:**
1. Push changes to GitHub `main` branch
2. GitHub Actions builds and pushes new images to GHCR
3. Watchtower detects new images and updates containers
4. Services restart automatically with zero downtime

**Manual update:**
```bash
docker compose pull
docker compose up -d
```

## GHCR Authentication

### Public Images (Recommended)

Make your GHCR packages public:
- Go to https://github.com/YOURUSERNAME?tab=packages
- Select package → Settings → Change visibility → Public

No authentication needed.

### Private Images

1. Create GitHub Personal Access Token with `read:packages` scope
2. Login to GHCR on VPS:
   ```bash
   echo YOUR_PAT | docker login ghcr.io -u YOUR_USERNAME --password-stdin
   ```
3. Uncomment `WATCHTOWER_REGISTRY_AUTH=true` in `docker-compose.yml`

## Backups

### Automated Backups (Recommended)

```bash
# Edit crontab
crontab -e

# Nightly database backup at 2 AM
0 2 * * * sqlite3 /srv/hnh-map/grids.db ".backup '/srv/hnh-map/backups/grids-$(date +\%F).db'"

# Cleanup old backups (30 days) at 3 AM
0 3 * * * find /srv/hnh-map/backups -name "grids-*.db" -mtime +30 -delete

# Weekly tenant storage backup (Sundays at 3 AM)
0 3 * * 0 tar -czf /srv/hnh-map/backups/tenant-storage-$(date +\%F).tar.gz -C /srv/hnh-map tenants/
```

**Note:** Multi-tenant version stores tiles in `/srv/hnh-map/tenants/{tenantId}/grids/`

### Manual Backup

```bash
# Database only
sqlite3 /srv/hnh-map/grids.db ".backup '/srv/hnh-map/backups/manual-$(date +%F).db'"

# Tenant storage only
tar -czf /srv/hnh-map/backups/tenant-storage-$(date +%F).tar.gz -C /srv/hnh-map tenants/

# Full backup (database + tenant storage + keys)
tar -czf backup-$(date +%F).tar.gz -C /srv hnh-map/
```

## Security

### Essential Steps

1. **Change admin password** immediately after first login
2. **Enable firewall:**
   ```bash
   sudo ufw enable
   sudo ufw allow ssh
   sudo ufw allow 80/tcp
   ```
3. **Set permissions:**
   ```bash
   chmod 750 /srv/hnh-map
   chmod 640 /srv/hnh-map/grids.db
   ```
4. **Enable auto-updates:**
   ```bash
   sudo apt install -y unattended-upgrades
   sudo dpkg-reconfigure -plow unattended-upgrades
   ```
5. **Review security settings:** See [SECURITY.md](SECURITY.md) for detailed security configuration

## Maintenance

### View Logs
```bash
docker compose logs -f           # All services
docker compose logs -f api       # API only
docker compose logs -f web       # Web only
```

### Restart Services
```bash
docker compose restart           # All services
docker compose restart api       # API only
```

### Stop Services
```bash
docker compose down              # Stop all
docker compose up -d             # Start all
```

### Database Maintenance
```bash
# Check integrity
sqlite3 /srv/hnh-map/grids.db "PRAGMA integrity_check;"

# Optimize
sqlite3 /srv/hnh-map/grids.db "VACUUM;"
```

### Disk Cleanup
```bash
# Check usage
df -h
docker system df

# Clean up unused Docker resources
docker system prune -a
```

## Systemd Service (Auto-start on Boot)

Create `/etc/systemd/system/hnhmap.service`:

```ini
[Unit]
Description=HnH Mapper Docker Stack
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/hnhmap
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down

[Install]
WantedBy=multi-user.target
```

Enable:
```bash
sudo systemctl daemon-reload
sudo systemctl enable hnhmap.service
sudo systemctl start hnhmap.service
```

## Future Domain Setup

When you get a domain:

1. Point DNS A record to VPS IP
2. Edit `Caddyfile`: replace `:80 {` with `yourdomain.com {`
3. Restart Caddy: `docker compose restart caddy`
4. Caddy automatically provisions HTTPS with Let's Encrypt

No other changes needed!

## Troubleshooting

### Containers won't start
```bash
docker compose logs api
docker compose logs web
```

### Login not working
- Check both API and Web are running: `docker compose ps`
- Verify shared volume: `ls -la /srv/hnh-map/DataProtection-Keys/`

### Watchtower not updating
```bash
docker compose logs watchtower
docker pull ghcr.io/YOUR_USERNAME/hnhmapper-api:main
```

### Database locked
```bash
lsof /srv/hnh-map/grids.db
docker compose restart
```

## Documentation

**Deployment Guides:**
- **`FIRST-TIME-SETUP.md`** - Step-by-step first-time deployment guide with multi-tenancy setup
- **`VPS-SETUP.md`** - Comprehensive VPS setup and deployment guide
- **`SECURITY.md`** - Security configuration and hardening guide

**Project Documentation:**
- `README.md` - Project overview
- `CLAUDE.md` - Technical documentation for AI assistants
- `DEPLOYMENT.md` - Deployment architecture and CI/CD overview
- `DATABASE_SCHEMA.md` - Complete database schema documentation
- `MULTI_TENANCY_DESIGN.md` - Multi-tenancy architecture design

## Support

If you encounter issues:
1. Check logs: `docker compose logs -f`
2. Verify firewall: `sudo ufw status`
3. Check disk space: `df -h`
4. Review `VPS-SETUP.md` for missed steps

