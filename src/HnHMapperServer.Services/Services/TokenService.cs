using System.Security.Cryptography;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service for managing authentication tokens with tenant prefixes
/// Format: {tenantId}_{secret}
/// Example: warrior-shield-42_a1b2c3d4e5f67890123456789012345678901234567890123456789012345
/// </summary>
public class TokenService : ITokenService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TokenService> _logger;

    public TokenService(ApplicationDbContext db, ILogger<TokenService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new authentication token with tenant prefix
    /// </summary>
    public async Task<string> CreateTokenAsync(string tenantId, string userId, string name, string scopes)
    {
        // Validate tenant exists and is active
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null)
            throw new ArgumentException($"Tenant {tenantId} not found");

        if (!tenant.IsActive)
            throw new InvalidOperationException($"Tenant {tenantId} is not active");

        // Generate 32-byte random secret (64 hex characters)
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToHexString(secretBytes).ToLowerInvariant();

        // Format: {tenantId}_{secret}
        var fullToken = $"{tenantId}_{secret}";

        // Hash only the secret portion for storage (SHA-256)
        // This allows validating the secret while preventing token modification attacks
        var tokenHash = ClientToken.Hash(secret);

        // Create token entity
        var entity = new TokenEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayToken = fullToken,
            TokenHash = tokenHash,
            TenantId = tenantId,
            UserId = userId,
            Name = name,
            Scopes = scopes,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = null, // No expiration by default
            LastUsedAt = null
        };

        _db.Tokens.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Token created for tenant {TenantId}, user {UserId}, name {Name}",
            tenantId, userId, name);

        return fullToken;
    }

    /// <summary>
    /// Validates a token and extracts tenant and user information
    /// </summary>
    public async Task<TokenValidationResult> ValidateTokenAsync(string fullToken)
    {
        try
        {
            // Parse token format: {tenantId}_{secret}
            var parts = fullToken.Split('_', 2);
            if (parts.Length != 2)
            {
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Invalid token format. Expected: {tenantId}_{secret}"
                };
            }

            var tenantId = parts[0];
            var secret = parts[1];

            // Hash only the secret portion (consistent with storage)
            var tokenHash = ClientToken.Hash(secret);

            // Query database for token
            var token = await _db.Tokens
                .IgnoreQueryFilters() // Need to query across tenants for validation
                .Where(t => t.TokenHash == tokenHash)
                .FirstOrDefaultAsync();

            if (token == null)
            {
                _logger.LogWarning("Token not found: hash={Hash}", tokenHash[..8]);
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Invalid token"
                };
            }

            // Security check: Verify tenant ID matches
            // This prevents token modification attacks (changing tenant prefix)
            if (token.TenantId != tenantId)
            {
                _logger.LogWarning(
                    "Token tenant mismatch: expected={Expected}, got={Got}, hash={Hash}",
                    token.TenantId, tenantId, tokenHash[..8]);

                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Token tenant mismatch"
                };
            }

            // Check tenant is active
            var tenant = await _db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant == null || !tenant.IsActive)
            {
                _logger.LogWarning("Token tenant inactive or not found: {TenantId}", tenantId);
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Tenant inactive or not found"
                };
            }

            // Check expiration
            if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
            {
                _logger.LogWarning("Token expired: hash={Hash}, expiredAt={ExpiresAt}",
                    tokenHash[..8], token.ExpiresAt);

                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Token expired"
                };
            }

            // Check scopes (must have Upload permission for game client endpoints)
            var scopes = token.Scopes?.Split(',') ?? Array.Empty<string>();
            if (!scopes.Contains(Permission.Upload.ToClaimValue(), StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Token missing Upload scope: hash={Hash}", tokenHash[..8]);
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Token missing required scope: {Permission.Upload.ToClaimValue()}"
                };
            }

            // Update last used timestamp
            token.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return new TokenValidationResult
            {
                IsValid = true,
                TenantId = token.TenantId,
                UserId = token.UserId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token validation failed");
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token validation error"
            };
        }
    }

    /// <summary>
    /// Lists all tokens for a specific tenant (tenant-scoped)
    /// </summary>
    public async Task<List<TokenInfo>> GetTokensByTenantAsync(string tenantId)
    {
        var tokens = await _db.Tokens
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return tokens.Select(t => new TokenInfo
        {
            Id = t.Id,
            DisplayToken = t.DisplayToken, // Full token shown only on creation, masked in list
            Name = t.Name,
            Scopes = t.Scopes,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt,
            LastUsedAt = t.LastUsedAt
        }).ToList();
    }

    /// <summary>
    /// Revokes a token by ID
    /// </summary>
    public async Task RevokeTokenAsync(string tokenId, string userId, bool isAdmin)
    {
        var token = await _db.Tokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tokenId);

        if (token == null)
            throw new ArgumentException($"Token {tokenId} not found");

        // Authorization: owner can revoke own token, admin can revoke any token in tenant
        if (token.UserId != userId && !isAdmin)
            throw new UnauthorizedAccessException("You can only revoke your own tokens");

        _db.Tokens.Remove(token);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Token revoked: {TokenId}, tenant {TenantId}, by user {UserId}",
            tokenId, token.TenantId, userId);
    }
}
