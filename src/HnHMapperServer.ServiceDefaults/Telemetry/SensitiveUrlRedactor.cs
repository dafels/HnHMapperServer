using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace HnHMapperServer.ServiceDefaults.Telemetry;

/// <summary>
/// Masks bearer secrets carried in URL paths: game-client upload tokens (<c>/client/{tenant}_{secret}</c> -
/// clients are fixed, the token cannot move to a header), invite codes (<c>/invite/{code}</c>,
/// <c>/api/invitations/validate/{code}</c>, <c>/api/invitations/{code}/redeem</c>), token-revoke paths
/// (<c>/api/tenants/{t}/tokens/{token}</c>) and outbound Discord webhook tokens (<c>/api/webhooks/{id}/{token}</c>).
/// The non-secret prefix (tenant id, webhook id) is kept so a 401 can still be attributed to a tenant.
/// <para>This is THE list of secret-bearing URLs, inbound routes and outbound HttpClient targets alike: extend it
/// and SensitiveUrlRedactorTests when adding one. Used by <see cref="SensitiveUrlRedactionEnricher"/> (every log
/// sink) and <see cref="SensitiveUrlRedactionProcessor"/> (every span).</para>
/// </summary>
public static partial class SensitiveUrlRedactor
{
    public const string Mask = "***";

    // Cheap vectorized pre-check: almost no logged string contains any of these, so the regex rarely runs.
    private static readonly SearchValues<string> Markers = SearchValues.Create(
        ["/client/", "/invite/", "/invitations/", "/tokens/", "/webhooks/"], StringComparison.OrdinalIgnoreCase);

    // A secret ends at / ? # whitespace, a quote, < > , ; ) ] so free text after it survives
    // ("... responded 401 in 3 ms", a quoted Scope element). Tokens are hex, invite codes GUIDs and
    // webhook tokens [A-Za-z0-9_-], so none of these characters can be part of a secret.
    private const string End = """/?#\s"'`<>,;)\]""";
    private const string SegmentChar = "[^" + End + "]";
    // A segment starting with '{' is a route template (/client/{token}) and is left alone.
    private const string SecretStart = "[^{" + End + "]";
    private const string Secret = SecretStart + SegmentChar + "*";

    // '/client/+' also covers '/client//<token>' (TenantContextMiddleware splits with RemoveEmptyEntries).
    // Tenant = text before the FIRST '_', like TenantContextMiddleware. IgnoreCase: routing is case-insensitive.
    private const string Pattern =
        "(?<prefix>/client/+|/api/tenants/" + SegmentChar + "+/tokens/)(?:(?<tenant>[^_{" + End + "][^_" + End + "]*)_)?" + Secret
        + "|(?<prefix>/invite/|/api/invitations/validate/)" + Secret
        + "|(?<prefix>/api/invitations/)" + Secret + "(?<suffix>/redeem)"
        + @"|(?<prefix>/api/(?:v\d+/)?webhooks/" + SegmentChar + "+/)" + Secret;

    [GeneratedRegex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegment();

    /// <summary>
    /// Returns true and the masked value when <paramref name="value"/> carries a secret; false (no allocation)
    /// otherwise, including for values that are already masked.
    /// </summary>
    public static bool TryRedact(string? value, [NotNullWhen(true)] out string? redacted)
    {
        redacted = null;
        if (string.IsNullOrEmpty(value) || !value.AsSpan().ContainsAny(Markers))
            return false;

        var result = SecretSegment().Replace(value, static m =>
            m.Groups["prefix"].Value
            + (m.Groups["tenant"].Success ? m.Groups["tenant"].Value + "_" : "")
            + Mask
            + m.Groups["suffix"].Value);

        if (string.Equals(result, value, StringComparison.Ordinal))
            return false; // idempotent on masked values

        redacted = result;
        return true;
    }
}
