using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service to migrate existing tokens to the new tenant-prefixed format.
/// Runs at application startup.
/// </summary>
public class TokenMigrationService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TokenMigrationService> _logger;
    private const string DefaultTenantId = "default-tenant-1";

    public TokenMigrationService(ApplicationDbContext db, ILogger<TokenMigrationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Migrates existing tokens to the new format: {tenantId}_{secret}
    /// </summary>
    public async Task MigrateTokensAsync()
    {
        _logger.LogInformation("Starting token format migration...");

        try
        {
            // Find tokens that need migration
            // Tokens needing migration will have DisplayToken without underscore (old format)
            var tokensToMigrate = await _db.Tokens
                .IgnoreQueryFilters()
                .Where(t => !t.DisplayToken.Contains("_"))
                .ToListAsync();

            if (tokensToMigrate.Count == 0)
            {
                _logger.LogInformation("No tokens need migration. All tokens are already in the new format.");
                return;
            }

            _logger.LogInformation("Found {Count} tokens to migrate", tokensToMigrate.Count);

            int successCount = 0;
            int errorCount = 0;

            foreach (var token in tokensToMigrate)
            {
                try
                {
                    // Old format: token.DisplayToken is just the secret
                    var oldSecret = token.DisplayToken;

                    // New format: {tenantId}_{secret}
                    var newFullToken = $"{DefaultTenantId}_{oldSecret}";

                    // Hash only the secret: that is what validation hashes (ClientToken.Hash).
                    // Hashing the full {tenant}_{secret} string made every migrated token
                    // impossible to validate.
                    var newHash = ClientToken.Hash(oldSecret);

                    // Update token
                    token.DisplayToken = newFullToken;
                    token.TokenHash = newHash;

                    // Ensure TenantId is set
                    if (string.IsNullOrEmpty(token.TenantId))
                    {
                        token.TenantId = DefaultTenantId;
                    }

                    successCount++;

                    _logger.LogDebug(
                        "Migrated token {TokenId} for user {UserId} in tenant {TenantId}",
                        token.Id, token.UserId, token.TenantId);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex,
                        "Failed to migrate token {TokenId} for user {UserId}",
                        token.Id, token.UserId);
                }
            }

            // Save all changes
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Token migration completed. Success: {Success}, Errors: {Errors}",
                successCount, errorCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token migration failed");
            throw;
        }
    }
}
