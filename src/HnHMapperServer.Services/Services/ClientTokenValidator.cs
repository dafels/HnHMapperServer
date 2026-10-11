using HnHMapperServer.Core.Enums;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Outcome of <see cref="ClientTokenValidator.ValidateAsync"/>. <see cref="TokenHash"/> is null only
/// for an empty secret; <see cref="TenantExists"/> says whether the tenant prefix names a real
/// tenant (what decides whether a rejection may be attributed to it in the logs).
/// </summary>
public readonly record struct ClientTokenValidation(
    bool IsValid,
    ClientTokenRejectionReason? Rejection,
    string? TokenHash,
    bool TenantExists);

/// <summary>
/// The tenant middleware's /client token check, moved out of the Api so it can be tested against
/// real SQLite. Same sequence and queries as before (empty secret → hash → token row by hash AND
/// tenant → expiry → tenant active), with a typed reason instead of a log line: it never logs —
/// <see cref="ClientTokenRejectionTracker"/> decides what is worth a Warning.
/// </summary>
public sealed class ClientTokenValidator
{
    private readonly ApplicationDbContext _db;

    public ClientTokenValidator(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ClientTokenValidation> ValidateAsync(string tenantId, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new ClientTokenValidation(false, ClientTokenRejectionReason.EmptySecret, null, false);
        }

        var hash = ClientToken.Hash(secret);

        // IgnoreQueryFilters() is required: this establishes the tenant context, so the global
        // tenant filter would otherwise reject every token. No cancellation token, as before: a
        // cancelled lookup would surface as a 500 request instead of the client's 401.
        var token = await _db.Tokens
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.TenantId == tenantId);

        if (token == null)
        {
            // Only on the reject path: one primary-key lookup.
            var tenantExists = await _db.Tenants
                .IgnoreQueryFilters()
                .AnyAsync(t => t.Id == tenantId);
            return new ClientTokenValidation(false, ClientTokenRejectionReason.NotFound, hash, tenantExists);
        }

        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
        {
            // The token row's foreign key guarantees the tenant exists.
            return new ClientTokenValidation(false, ClientTokenRejectionReason.Expired, hash, true);
        }

        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.IsActive)
        {
            return new ClientTokenValidation(false, ClientTokenRejectionReason.TenantInactive, hash, tenant != null);
        }

        return new ClientTokenValidation(true, null, hash, true);
    }
}
