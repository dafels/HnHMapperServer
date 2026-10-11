using HnHMapperServer.Core.Enums;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace HnHMapperServer.Api.Middleware;

/// <summary>
/// Middleware that extracts the tenant ID from the request and stores it in HttpContext.Items
/// Supports two strategies:
/// 1. Extract from token in URL path (/client/{tenantId}_{secret}/...)
/// 2. Extract from authentication claims (TenantId claim for web UI)
/// </summary>
public class TenantContextMiddleware
{
    /// <summary>Tag added to http.server.request.duration for every 401 this middleware answers.</summary>
    public const string RejectionMetricTag = "hnhmapper.client_token.rejection";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;
    private readonly ClientTokenRejectionTracker _rejections;

    public TenantContextMiddleware(
        RequestDelegate next,
        ILogger<TenantContextMiddleware> logger,
        ClientTokenRejectionTracker rejections)
    {
        _next = next;
        _logger = logger;
        _rejections = rejections;
    }

    public async Task InvokeAsync(HttpContext context, ClientTokenValidator validator)
    {
        string? tenantId = null;

        // Strategy 1: Extract from token in URL (game clients)
        // Path format: /client/{tenantId}_{secret}/...
        if (context.Request.Path.StartsWithSegments("/client"))
        {
            var pathParts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts != null && pathParts.Length >= 2)
            {
                // Token must be in format {tenantId}_{secret}
                if (!ClientToken.TrySplit(pathParts[1], out var extractedTenantId, out var secret))
                {
                    OnRejected(context, ClientTokenRejectionReason.MalformedToken, string.Empty, tokenHash: null, attributable: false);
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid token format. Tokens must include tenant prefix." });
                    return;
                }

                var validation = await validator.ValidateAsync(extractedTenantId, secret);
                if (!validation.IsValid)
                {
                    // Attributed to the tenant only when the tenant exists and the secret has the
                    // shape we generate — anything else is noise, pooled without a tenant name.
                    OnRejected(context, validation.Rejection!.Value, extractedTenantId, validation.TokenHash,
                        attributable: validation.TenantExists && ClientToken.IsCanonicalSecret(secret));
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid token" });
                    return;
                }

                tenantId = extractedTenantId;
                _logger.LogDebug("Tenant context resolved from token: {TenantId}", tenantId);
            }
        }
        // Strategy 2: Extract from claims (web UI)
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            tenantId = context.User.FindFirst("TenantId")?.Value;

            if (!string.IsNullOrEmpty(tenantId))
            {
                _logger.LogDebug("Tenant context resolved from claims: {TenantId}", tenantId);
            }
            else
            {
                // Normal for users who have not created/joined a tenant yet (they are routed to the create-or-join screen)
                _logger.LogDebug("User is authenticated but has no TenantId claim (no active tenant)");
            }
        }
        else
        {
            _logger.LogDebug("User is not authenticated for request: {Path}", context.Request.Path);
        }

        // Store tenant ID in context for downstream use
        if (!string.IsNullOrEmpty(tenantId))
        {
            context.Items["TenantId"] = tenantId;
        }
        else
        {
            _logger.LogDebug("No tenant context available for request: {Path}", context.Request.Path);
        }

        await _next(context);
    }

    /// <summary>
    /// Counts and logs one rejected /client request. Every request: a fixed-value metric tag on
    /// http.server.request.duration (exact counts per route and reason) and a Debug line. Warnings
    /// only when the tracker says so — first sighting of a (tenant, token) and a summary per
    /// interval while it keeps coming. No log line ever carries the token, its hash or the client IP;
    /// the tenant id is named only for attributable rejections (an existing tenant, a canonical secret).
    /// </summary>
    private void OnRejected(HttpContext context, ClientTokenRejectionReason reason, string tenantId, string? tokenHash, bool attributable)
    {
        context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new(RejectionMetricTag, MetricTagValue(reason)));

        // Routing ran before this middleware (WebApplication adds UseRouting first), so the matched
        // endpoint's template — "/client/{token}/positionUpdate", never the token itself — is known.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";

        _logger.LogDebug("Client token rejected ({Reason}) for tenant prefix {TenantId} on {Route}",
            reason, tenantId.Length > 64 ? tenantId[..64] : tenantId, route);

        if (attributable && tokenHash != null)
        {
            var decision = _rejections.RecordAttributed(tenantId, tokenHash, reason);
            switch (decision.Action)
            {
                case RejectionLogAction.First when decision.Folded:
                    _logger.LogWarning(
                        "Game clients of tenant {TenantId} send more than {Cap} distinct rejected tokens ({Reason}, on {Route}); further ones are counted together, summarized every {SummaryMinutes} min",
                        tenantId, _rejections.Options.PerTenantCap, reason, route, _rejections.Options.SummaryInterval.TotalMinutes);
                    break;
                case RejectionLogAction.First:
                    _logger.LogWarning(
                        "Game client of tenant {TenantId} sends a rejected token ({Reason}) on {Route} - the client needs its member's current Mapper URL; repeats are summarized every {SummaryMinutes} min",
                        tenantId, reason, route, _rejections.Options.SummaryInterval.TotalMinutes);
                    break;
                case RejectionLogAction.Summary:
                    _logger.LogWarning(
                        "Game client of tenant {TenantId} still sends a rejected token ({Reason}): {Count} requests since {Since:o}, last on {Route}",
                        tenantId, reason, decision.Count, decision.Since, route);
                    break;
            }
        }
        else
        {
            var decision = _rejections.RecordUnattributed(reason);
            if (decision.Action != RejectionLogAction.None)
            {
                _logger.LogWarning(
                    "{Count} game-client request(s) rejected ({Reason}) since {Since:o}, last on {Route} (not attributed to a tenant)",
                    decision.Count, reason, decision.Since, route);
            }
        }
    }

    /// <summary>A fixed set: never derive a tag value from the request (attacker-controlled cardinality).</summary>
    private static string MetricTagValue(ClientTokenRejectionReason reason) => reason switch
    {
        ClientTokenRejectionReason.MalformedToken => "malformed",
        ClientTokenRejectionReason.EmptySecret => "empty_secret",
        ClientTokenRejectionReason.NotFound => "not_found",
        ClientTokenRejectionReason.Expired => "expired",
        ClientTokenRejectionReason.TenantInactive => "tenant_inactive",
        _ => "other"
    };
}
