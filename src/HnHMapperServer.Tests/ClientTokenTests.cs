using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// ClientToken: the one definition of the /client token scheme ({tenantId}_{secret}, SHA-256 of
/// the secret) that creation, endpoint validation, the tenant middleware and the legacy migration
/// share. The split semantics are the middleware's (first underscore, index &gt; 0).
/// </summary>
public class ClientTokenTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TrySplit_HyphenatedTenantWithSingleLetterSegment()
    {
        Assert.True(ClientToken.TrySplit("sketchbook-pot-u-2046_" + Secret, out var tenant, out var secret));
        Assert.Equal("sketchbook-pot-u-2046", tenant);
        Assert.Equal(Secret, secret);
    }

    [Theory]
    [InlineData("notoken")]
    [InlineData("")]
    [InlineData("_abc")]          // underscore at index 0
    [InlineData("__global___x")]  // the pseudo-tenant id starts with an underscore
    public void TrySplit_NoUsablePrefix_False(string segment)
    {
        Assert.False(ClientToken.TrySplit(segment, out var tenant, out var secret));
        Assert.Equal(string.Empty, tenant);
        Assert.Equal(string.Empty, secret);
    }

    [Fact]
    public void TrySplit_TrailingUnderscore_TrueWithEmptySecret()
    {
        Assert.True(ClientToken.TrySplit("tenant-a-1_", out var tenant, out var secret));
        Assert.Equal("tenant-a-1", tenant);
        Assert.Equal(string.Empty, secret);
    }

    [Fact]
    public void TrySplit_SplitsAtFirstUnderscoreOnly()
    {
        Assert.True(ClientToken.TrySplit("a_b_c", out var tenant, out var secret));
        Assert.Equal("a", tenant);
        Assert.Equal("b_c", secret);
    }

    [Theory]
    [InlineData(Secret, true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)] // uppercase
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]  // 63
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0", false)] // 65
    [InlineData("", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)] // non-hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde ", false)] // trailing space
    public void IsCanonicalSecret(string secret, bool expected)
    {
        Assert.Equal(expected, ClientToken.IsCanonicalSecret(secret));
    }

    [Fact]
    public void Hash_IsLowercaseSha256HexOfTheUtf8Secret()
    {
        // SHA-256("abc"), the FIPS 180-2 test vector.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ClientToken.Hash("abc"));
    }
}
