namespace HnHMapperServer.Web.Services;

/// <summary>
/// Builds the URL of the Web-side tenant switch endpoint (<c>/api/tenant/select</c>). Switching re-issues the
/// auth cookie, which a Blazor circuit cannot do, so callers must navigate here with <c>forceLoad: true</c>.
/// </summary>
public static class TenantSwitchUrl
{
    public static string For(string tenantId, string? returnUrl = null)
    {
        var url = $"/api/tenant/select?tenantId={Uri.EscapeDataString(tenantId)}";
        if (!string.IsNullOrEmpty(returnUrl))
            url += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return url;
    }
}
