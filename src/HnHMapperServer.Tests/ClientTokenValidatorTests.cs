using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// ClientTokenValidator — the tenant middleware's /client token check, moved into Services — against
/// real SQLite (temp file, Pooling=False; FKs from Tokens to Tenants and AspNetUsers). The context is
/// built WITHOUT an IHttpContextAccessor, like the middleware before tenant context exists: the
/// queries must carry IgnoreQueryFilters + explicit predicates. Also pins the shared hash scheme
/// (TokenService stores what the validator computes) and the TokenMigrationService fix.
/// </summary>
public class ClientTokenValidatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly ClientTokenValidator _validator;
    private readonly TokenService _tokens;

    private const string Hyphenated = "sketchbook-pot-u-2046";
    private const string Other = "other-tenant-1";
    private const string Legacy = "default-tenant-1";
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    public ClientTokenValidatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-token-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        foreach (var id in new[] { Hyphenated, Other, Legacy })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = id, Name = id, StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            });
        }
        _db.Users.Add(new ApplicationUser { Id = UserA, UserName = "alice", NormalizedUserName = "ALICE" });
        _db.Users.Add(new ApplicationUser { Id = UserB, UserName = "bob", NormalizedUserName = "BOB" });
        _db.SaveChanges();

        _validator = new ClientTokenValidator(_db);
        _tokens = new TokenService(_db, Mock.Of<ILogger<TokenService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private async Task<(string Tenant, string Secret)> CreateAsync(string tenantId, string userId, string name = "client")
    {
        var full = await _tokens.CreateTokenAsync(tenantId, userId, name, "upload");
        Assert.True(ClientToken.TrySplit(full, out var tenant, out var secret));
        return (tenant, secret);
    }

    [Fact]
    public async Task CreatedToken_ForHyphenatedTenant_IsValid_AndHashEqualsStored()
    {
        var (tenant, secret) = await CreateAsync(Hyphenated, UserA);

        var result = await _validator.ValidateAsync(tenant, secret);

        Assert.True(result.IsValid);
        Assert.Null(result.Rejection);
        Assert.True(result.TenantExists);
        Assert.True(ClientToken.IsCanonicalSecret(secret));
        var row = await _db.Tokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.TenantId == Hyphenated);
        Assert.Equal(row.TokenHash, ClientToken.Hash(secret));
        Assert.Equal(row.TokenHash, result.TokenHash);
        // The endpoint-level validation (HasUploadAsync) agrees with the middleware's.
        Assert.True((await _tokens.ValidateTokenAsync($"{tenant}_{secret}")).IsValid);
    }

    [Fact]
    public async Task SameSecret_OtherExistingTenantPrefix_IsNotFound_TenantExists()
    {
        var (_, secret) = await CreateAsync(Hyphenated, UserA);

        var result = await _validator.ValidateAsync(Other, secret);

        Assert.False(result.IsValid);
        Assert.Equal(ClientTokenRejectionReason.NotFound, result.Rejection);
        Assert.True(result.TenantExists);
    }

    [Fact]
    public async Task UnknownTenantPrefix_IsNotFound_TenantDoesNotExist()
    {
        var (_, secret) = await CreateAsync(Hyphenated, UserA);

        var result = await _validator.ValidateAsync("nonexistent-tenant-1", secret);

        Assert.Equal(ClientTokenRejectionReason.NotFound, result.Rejection);
        Assert.False(result.TenantExists);
    }

    [Fact]
    public async Task UppercasedSecret_IsNotFound_NoNormalization()
    {
        var (tenant, secret) = await CreateAsync(Hyphenated, UserA);

        var result = await _validator.ValidateAsync(tenant, secret.ToUpperInvariant());

        Assert.Equal(ClientTokenRejectionReason.NotFound, result.Rejection);
        Assert.True(result.TenantExists);
    }

    [Fact]
    public async Task RowDeletedLikeTenantAdminRevoke_IsNotFound()
    {
        var (tenant, secret) = await CreateAsync(Hyphenated, UserA);
        // TenantAdminEndpoints' revoke: Remove + SaveChanges (TokenService.RevokeTokenAsync has no callers).
        var row = await _db.Tokens.IgnoreQueryFilters().SingleAsync(t => t.TenantId == Hyphenated);
        _db.Tokens.Remove(row);
        await _db.SaveChangesAsync();

        var result = await _validator.ValidateAsync(tenant, secret);

        Assert.Equal(ClientTokenRejectionReason.NotFound, result.Rejection);
        Assert.True(result.TenantExists);
        Assert.Equal(ClientToken.Hash(secret), result.TokenHash);
    }

    [Fact]
    public async Task MemberRemoved_AllTheirTokensDeleted_IsNotFound_OtherMemberUnaffected()
    {
        var (tenant, secretA) = await CreateAsync(Hyphenated, UserA, "a");
        var (_, secretB) = await CreateAsync(Hyphenated, UserB, "b");
        // TenantAdminEndpoints' member removal: RemoveRange of that user's tokens in the tenant.
        _db.Tokens.RemoveRange(_db.Tokens.IgnoreQueryFilters().Where(t => t.TenantId == Hyphenated && t.UserId == UserA));
        await _db.SaveChangesAsync();

        Assert.Equal(ClientTokenRejectionReason.NotFound, (await _validator.ValidateAsync(tenant, secretA)).Rejection);
        Assert.True((await _validator.ValidateAsync(tenant, secretB)).IsValid);
    }

    [Fact]
    public async Task ExpiresAtInPast_IsExpired()
    {
        var (tenant, secret) = await CreateAsync(Hyphenated, UserA);
        var row = await _db.Tokens.IgnoreQueryFilters().SingleAsync(t => t.TenantId == Hyphenated);
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); // nothing writes ExpiresAt in production today
        await _db.SaveChangesAsync();

        var result = await _validator.ValidateAsync(tenant, secret);

        Assert.Equal(ClientTokenRejectionReason.Expired, result.Rejection);
        Assert.True(result.TenantExists);
    }

    [Fact]
    public async Task InactiveTenant_IsTenantInactive_TenantExists()
    {
        var (tenant, secret) = await CreateAsync(Hyphenated, UserA);
        var row = await _db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == Hyphenated);
        row.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await _validator.ValidateAsync(tenant, secret);

        Assert.Equal(ClientTokenRejectionReason.TenantInactive, result.Rejection);
        Assert.True(result.TenantExists);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceSecret_IsEmptySecret_WithoutHash(string secret)
    {
        var result = await _validator.ValidateAsync(Hyphenated, secret);

        Assert.Equal(ClientTokenRejectionReason.EmptySecret, result.Rejection);
        Assert.Null(result.TokenHash);
        Assert.False(result.TenantExists);
    }

    [Fact]
    public async Task TokenMigrationService_MigratedLegacyToken_Validates()
    {
        // A pre-tenancy row: DisplayToken is the bare secret. The migration used to hash the full
        // "{tenant}_{secret}", which validation never computes — a migrated token could never work.
        const string oldSecret = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        _db.Tokens.Add(new TokenEntity
        {
            Id = Guid.NewGuid().ToString("N"), DisplayToken = oldSecret, TokenHash = "legacy",
            TenantId = Legacy, UserId = UserA, Name = "legacy", Scopes = "upload", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        await new TokenMigrationService(_db, Mock.Of<ILogger<TokenMigrationService>>()).MigrateTokensAsync();

        var row = await _db.Tokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Name == "legacy");
        Assert.Equal($"{Legacy}_{oldSecret}", row.DisplayToken);
        Assert.True((await _validator.ValidateAsync(Legacy, oldSecret)).IsValid);
    }
}
