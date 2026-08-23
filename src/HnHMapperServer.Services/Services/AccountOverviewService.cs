using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Derives "how can this account sign in?" from the Identity tables: "Password" when a hash exists, plus one
/// entry per AspNetUserLogins provider. Shared by the superadmin overview and the per-tenant member lists.
/// </summary>
public static class SignInMethodResolver
{
    public const string PasswordMethod = RegistrationSources.Password;

    public sealed record Methods(List<string> Names, Dictionary<string, string> ExternalIds);

    public static async Task<Dictionary<string, Methods>> ResolveAsync(ApplicationDbContext db, IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        var result = userIds.Distinct().ToDictionary(id => id, _ => new Methods(new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        if (result.Count == 0)
            return result;

        var ids = result.Keys.ToList();

        var withPassword = await db.Users
            .Where(u => ids.Contains(u.Id) && u.PasswordHash != null)
            .Select(u => u.Id)
            .ToListAsync(ct);
        foreach (var id in withPassword)
            result[id].Names.Add(PasswordMethod);

        var logins = await db.UserLogins
            .Where(l => ids.Contains(l.UserId))
            .OrderBy(l => l.LoginProvider)
            .Select(l => new { l.UserId, l.LoginProvider, l.ProviderKey })
            .ToListAsync(ct);
        foreach (var login in logins)
        {
            var methods = result[login.UserId];
            if (!methods.Names.Contains(login.LoginProvider, StringComparer.OrdinalIgnoreCase))
                methods.Names.Add(login.LoginProvider);
            methods.ExternalIds[login.LoginProvider] = login.ProviderKey;
        }

        return result;
    }
}

public class AccountOverviewService : IAccountOverviewService
{
    private const int MaxPageSize = 100;
    private readonly ApplicationDbContext _db;

    public AccountOverviewService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<AccountOverviewPageDto> GetAccountsAsync(AccountOverviewQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        // Approved memberships in active tenants - the only ones that count as "has a map"
        var activeMemberships = _db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => tu.JoinedAt != default)
            .Join(_db.Tenants.IgnoreQueryFilters().Where(t => t.IsActive), tu => tu.TenantId, t => t.Id, (tu, t) => tu);

        var users = _db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            users = users.Where(u => (u.NormalizedUserName != null && u.NormalizedUserName.Contains(term))
                                     || (u.DiscordName != null && u.DiscordName.ToUpper().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var source = query.Source.Trim();
            users = users.Where(u => u.RegistrationSource == source);
        }

        if (!string.IsNullOrWhiteSpace(query.Method))
        {
            var method = query.Method.Trim();
            users = string.Equals(method, SignInMethodResolver.PasswordMethod, StringComparison.OrdinalIgnoreCase)
                ? users.Where(u => u.PasswordHash != null)
                : users.Where(u => _db.UserLogins.Any(l => l.UserId == u.Id && l.LoginProvider == method));
        }

        if (query.NoTenant)
            users = users.Where(u => !activeMemberships.Any(tu => tu.UserId == u.Id));

        if (!string.IsNullOrWhiteSpace(query.TenantId))
        {
            var tenantId = query.TenantId.Trim();
            users = users.Where(u => activeMemberships.Any(tu => tu.UserId == u.Id && tu.TenantId == tenantId));
        }

        var total = await users.CountAsync(ct);

        var pageUsers = await users
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var ids = pageUsers.Select(u => u.Id).ToList();
        var methods = await SignInMethodResolver.ResolveAsync(_db, ids, ct);

        var memberships = await _db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => ids.Contains(tu.UserId))
            .Join(_db.Tenants.IgnoreQueryFilters(), tu => tu.TenantId, t => t.Id,
                (tu, t) => new { tu.UserId, tu.TenantId, TenantName = t.Name, t.IsActive, tu.RoleString, tu.JoinedAt, tu.JoinSource, tu.InvitationId })
            .OrderBy(x => x.JoinedAt)
            .ToListAsync(ct);

        var superAdminRoleId = await _db.Roles
            .Where(r => r.Name == AuthorizationConstants.Roles.SuperAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);
        var superAdmins = superAdminRoleId == null
            ? new HashSet<string>()
            : (await _db.UserRoles.Where(ur => ur.RoleId == superAdminRoleId && ids.Contains(ur.UserId)).Select(ur => ur.UserId).ToListAsync(ct)).ToHashSet();

        var items = pageUsers.Select(u => new AccountOverviewDto
        {
            Id = u.Id,
            Username = u.UserName ?? string.Empty,
            DiscordName = u.DiscordName,
            CreatedAt = u.CreatedAt,
            RegistrationSource = u.RegistrationSource,
            SignInMethods = methods[u.Id].Names,
            ExternalIds = methods[u.Id].ExternalIds,
            LastLoginAt = u.LastLoginAt,
            IsSuperAdmin = superAdmins.Contains(u.Id),
            Memberships = memberships.Where(m => m.UserId == u.Id).Select(m => new AccountMembershipDto
            {
                TenantId = m.TenantId,
                TenantName = m.TenantName,
                Role = (string.IsNullOrEmpty(m.RoleString) ? "TenantUser" : m.RoleString),
                JoinedAt = m.JoinedAt,
                Pending = m.JoinedAt == default,
                TenantActive = m.IsActive,
                JoinSource = m.JoinSource,
                InvitationId = m.InvitationId
            }).ToList()
        }).ToList();

        return new AccountOverviewPageDto
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Summary = await BuildSummaryAsync(activeMemberships, ct)
        };
    }

    private async Task<AccountOverviewSummaryDto> BuildSummaryAsync(IQueryable<Core.Models.TenantUserEntity> activeMemberships, CancellationToken ct)
    {
        var bySource = await _db.Users
            .GroupBy(u => u.RegistrationSource)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byMethod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [SignInMethodResolver.PasswordMethod] = await _db.Users.CountAsync(u => u.PasswordHash != null, ct)
        };
        var providerCounts = await _db.UserLogins
            .GroupBy(l => l.LoginProvider)
            .Select(g => new { Provider = g.Key, Count = g.Select(l => l.UserId).Distinct().Count() })
            .ToListAsync(ct);
        foreach (var p in providerCounts)
            byMethod[p.Provider] = p.Count;

        return new AccountOverviewSummaryDto
        {
            Total = await _db.Users.CountAsync(ct),
            ByRegistrationSource = bySource.ToDictionary(x => string.IsNullOrEmpty(x.Source) ? "Unknown" : x.Source, x => x.Count),
            BySignInMethod = byMethod,
            WithoutTenant = await _db.Users.CountAsync(u => !activeMemberships.Any(tu => tu.UserId == u.Id), ct)
        };
    }
}
