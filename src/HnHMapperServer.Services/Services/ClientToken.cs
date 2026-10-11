using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// The game-client token scheme in one place: <c>{tenantId}_{secret}</c>, where only the secret's
/// SHA-256 is stored (Tokens.TokenHash). Token creation, endpoint validation, the tenant middleware
/// and the legacy migration all hash through <see cref="Hash"/>, so "same scheme everywhere" holds
/// by construction instead of by four private copies.
/// </summary>
public static class ClientToken
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>SHA-256 of the secret (UTF-8), lowercase hex — the TokenHash column's format.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>
    /// True for exactly 64 lowercase hex characters, the shape <c>TokenService.CreateTokenAsync</c>
    /// generates. Only such secrets are attributed to a tenant in the rejection logs: an arbitrary
    /// string is not a stale client, it is noise.
    /// </summary>
    public static bool IsCanonicalSecret(ReadOnlySpan<char> secret) =>
        secret.Length == 64 && !secret.ContainsAnyExcept(LowerHex);

    /// <summary>
    /// Splits <c>{tenantId}_{secret}</c> at the FIRST underscore (generated tenant ids never contain
    /// one — IconNames has none). False when there is no underscore or it is the first character;
    /// an empty secret still splits (and is rejected later as EmptySecret).
    /// </summary>
    public static bool TrySplit(string segment, out string tenantId, out string secret)
    {
        var i = segment.IndexOf('_');
        if (i <= 0)
        {
            tenantId = secret = string.Empty;
            return false;
        }

        tenantId = segment[..i];
        secret = segment[(i + 1)..];
        return true;
    }
}
