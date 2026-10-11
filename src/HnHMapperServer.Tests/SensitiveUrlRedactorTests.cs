using HnHMapperServer.ServiceDefaults.Telemetry;

namespace HnHMapperServer.Tests;

/// <summary>
/// SensitiveUrlRedactor is the single list of secret-bearing URLs (client upload tokens, invite codes,
/// token-revoke paths, Discord webhook tokens); without it they would reach RequestPath / url.path verbatim.
/// All values here are fake.
/// </summary>
public class SensitiveUrlRedactorTests
{
    // 64 hex chars, the shape of a client token secret
    private const string S = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string G = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    [Theory]
    // Game-client upload tokens: tenant prefix kept, secret masked
    [InlineData("/client/t-1_" + S + "/positionUpdate", "/client/t-1_***/positionUpdate")]
    [InlineData("/Client/t-1_" + S + "/gridUpload", "/Client/t-1_***/gridUpload")]
    [InlineData("/client//t-1_" + S + "/positionUpdate", "/client//t-1_***/positionUpdate")]
    [InlineData("/client/sketchbook-pot-u-2046_" + S + "/food", "/client/sketchbook-pot-u-2046_***/food")]
    [InlineData("/client/" + S + "/locate", "/client/***/locate")]
    [InlineData("/client/t-1_" + S, "/client/t-1_***")]
    [InlineData("/client/t-1_" + S + "/checkVersion?version=4", "/client/t-1_***/checkVersion?version=4")]
    [InlineData("/client/t-1_" + S + "#frag", "/client/t-1_***#frag")]
    [InlineData("http://api:8080/client/t-1_" + S + "/gridUpdate", "http://api:8080/client/t-1_***/gridUpdate")]
    // Whitespace, quotes, brackets and separators end the secret so the surrounding text survives
    [InlineData("HTTP POST /client/t-1_" + S + " responded 401 in 3.1 ms", "HTTP POST /client/t-1_*** responded 401 in 3.1 ms")]
    [InlineData("path=/client/t-1_" + S + "\tnext", "path=/client/t-1_***\tnext")]
    [InlineData("'/client/t-1_" + S + "'", "'/client/t-1_***'")]
    [InlineData("`/client/t-1_" + S + "`", "`/client/t-1_***`")]
    [InlineData("<a href=\"/client/t-1_" + S + "\">", "<a href=\"/client/t-1_***\">")]
    [InlineData("(/client/t-1_" + S + "), [/client/t-1_" + S + "]; ok", "(/client/t-1_***), [/client/t-1_***]; ok")]
    [InlineData("/client/t-1_" + S + ",/client/t-2_" + S, "/client/t-1_***,/client/t-2_***")]
    // IHttpClientFactory's "HTTP {HttpMethod} {Uri}" scope as Serilog renders it
    [InlineData("[\"HTTP POST http://api:8080/api/invitations/" + G + "/redeem\"]", "[\"HTTP POST http://api:8080/api/invitations/***/redeem\"]")]
    // Invite codes
    [InlineData("http://api:8080/api/invitations/validate/" + G, "http://api:8080/api/invitations/validate/***")]
    [InlineData("/api/invitations/" + G + "/redeem", "/api/invitations/***/redeem")]
    [InlineData("/invite/" + G, "/invite/***")]
    [InlineData("https://map.example/invite/" + G + "?x=1", "https://map.example/invite/***?x=1")]
    // Token revoke (keyed by the full display token)
    [InlineData("/api/tenants/t1/tokens/t1_" + S, "/api/tenants/t1/tokens/t1_***")]
    // Outbound Discord webhooks: webhook id kept, token masked
    [InlineData("https://discord.com/api/webhooks/123456/AbC-xyz_9", "https://discord.com/api/webhooks/123456/***")]
    [InlineData("https://discordapp.com/api/webhooks/1/tok?wait=true", "https://discordapp.com/api/webhooks/1/***?wait=true")]
    [InlineData("https://discord.com/api/v10/webhooks/1/tok/slack", "https://discord.com/api/v10/webhooks/1/***/slack")]
    public void Masks(string value, string expected)
    {
        Assert.True(SensitiveUrlRedactor.TryRedact(value, out var redacted));
        Assert.Equal(expected, redacted);
    }

    [Theory]
    // Route templates (span names, http.route, endpoint metadata) are left alone
    [InlineData("/client/{token}/positionUpdate")]
    [InlineData("/api/tenants/{tenantId}/tokens/{token}")]
    [InlineData("/api/invitations/{code}/redeem")]
    [InlineData("/api/invitations/validate/{code}")]
    [InlineData("/invite/{code}")]
    [InlineData("/api/tenants/{tenantId}/invitations/")]
    [InlineData("POST /client/{token}/gridUpload")]
    // Paths that carry no secret
    [InlineData("/map/api/v1/markers")]
    [InlineData("/api/tenants/t1/invitations")]
    [InlineData("/api/tenants/t1/invitations/5")]
    [InlineData("/api/user/tokens")]
    [InlineData("/api/tenants/t1/tokens")]
    [InlineData("/api/invitations")]
    [InlineData("/client")]
    [InlineData("/client/")]
    [InlineData("/client//")]
    [InlineData("https://discord.com/api/oauth2/token")]
    [InlineData("https://discord.com/api/webhooks/")]
    // Already masked: idempotent
    [InlineData("/client/t1_***/x")]
    [InlineData("/client/***/x")]
    [InlineData("https://discord.com/api/webhooks/1/***")]
    [InlineData("/api/invitations/***/redeem")]
    [InlineData("")]
    [InlineData(null)]
    public void Leaves_alone(string? value)
    {
        Assert.False(SensitiveUrlRedactor.TryRedact(value, out var redacted));
        Assert.Null(redacted);
    }
}
