using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Identity;

/// <summary>
/// Decides which of a user's memberships is the active one - the single tenant whose claims end up in the
/// auth cookie. Pure query helper: it never writes (it runs on every cookie revalidation), and both the Web and the
/// API claims factory go through it so the two processes can never disagree about the active tenant.
/// </summary>
public static class ActiveTenantMembershipResolver
{
    public sealed record Resolution(TenantUserEntity? Membership, IReadOnlyList<Permission> Permissions)
    {
        public static readonly Resolution None = new(null, Array.Empty<Permission>());
    }

    /// <summary>
    /// Candidates are approved memberships (JoinedAt != default) in active tenants. The membership matching
    /// <paramref name="activeTenantId"/> wins; otherwise the oldest membership (ties broken by row id) - a
    /// deterministic replacement for the historical "first row EF happens to return".
    /// </summary>
    public static async Task<Resolution> ResolveAsync(
        ApplicationDbContext db,
        string userId,
        string? activeTenantId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => tu.UserId == userId && tu.JoinedAt != default)
            .Join(
                db.Tenants.IgnoreQueryFilters().Where(t => t.IsActive),
                tu => tu.TenantId,
                t => t.Id,
                (tu, t) => tu)
            .OrderBy(tu => tu.JoinedAt)
            .ThenBy(tu => tu.Id)
            .ToListAsync(cancellationToken);

        var membership = (string.IsNullOrEmpty(activeTenantId)
                ? null
                : candidates.FirstOrDefault(tu => tu.TenantId == activeTenantId))
            ?? candidates.FirstOrDefault();

        if (membership == null)
            return Resolution.None;

        var permissions = await db.TenantPermissions
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantUserId == membership.Id)
            .Select(tp => tp.Permission)
            .ToListAsync(cancellationToken);

        return new Resolution(membership, permissions);
    }

    /// <summary>Active tenant id only (no permission load) - for cheap "is the cookie stale?" checks.</summary>
    public static async Task<string?> ResolveTenantIdAsync(
        ApplicationDbContext db,
        string userId,
        string? activeTenantId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => tu.UserId == userId && tu.JoinedAt != default)
            .Join(
                db.Tenants.IgnoreQueryFilters().Where(t => t.IsActive),
                tu => tu.TenantId,
                t => t.Id,
                (tu, t) => new { tu.TenantId, tu.JoinedAt, tu.Id })
            .OrderBy(x => x.JoinedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrEmpty(activeTenantId) && candidates.Any(c => c.TenantId == activeTenantId))
            return activeTenantId;

        return candidates.FirstOrDefault()?.TenantId;
    }
}
