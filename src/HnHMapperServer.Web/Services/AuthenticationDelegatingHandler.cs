using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// A delegating handler that properly forwards authentication cookies from the current HttpContext
/// to outgoing API requests. When HttpContext is not available (e.g., background threads),
/// falls back to using cached authentication state from the Blazor circuit.
/// This ensures that service-to-service calls maintain the user's authentication.
/// </summary>
public class AuthenticationDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthenticationDelegatingHandler> _logger;
    private readonly AuthenticationStateCache _authStateCache;

    public AuthenticationDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthenticationDelegatingHandler> logger,
        AuthenticationStateCache authStateCache)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _authStateCache = authStateCache;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        string? authCookiePrimary = null;
        string? authCookieLegacy = null;
        string? clientIp = null;
        string? clientScheme = null;
        bool usingCache = false;

        if (httpContext != null)
        {
            // Priority 1: Get the authentication cookies from the current HTTP request
            authCookiePrimary = httpContext.Request.Cookies["HnH.Auth"];
            authCookieLegacy = httpContext.Request.Cookies["HnHMapper.Auth"];
            clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
            clientScheme = httpContext.Request.Scheme;

            // Cache the primary cookie (and the browser's origin) for use by background threads (SSE reconnects, etc.)
            if (!string.IsNullOrEmpty(authCookiePrimary) && _authStateCache != null)
            {
                _authStateCache.CookieValue = authCookiePrimary;
            }
            if (_authStateCache != null && !string.IsNullOrEmpty(clientIp))
            {
                _authStateCache.ClientOrigin = (clientIp, clientScheme);
            }
        }
        else
        {
            // Priority 2: HttpContext is null (background thread), use cached cookie
            usingCache = true;
            if (_authStateCache != null)
            {
                authCookiePrimary = _authStateCache.CookieValue;
                (clientIp, clientScheme) = _authStateCache.ClientOrigin;
                if (string.IsNullOrEmpty(authCookiePrimary))
                {
                    _logger.LogWarning("HttpContext unavailable and AuthenticationStateCache.CookieValue is empty for {Method} {Uri}",
                        request.Method, request.RequestUri);
                }
                else
                {
                    _logger.LogDebug("Using cached authentication cookie for background thread request {Method} {Uri}",
                        request.Method, request.RequestUri);
                }
            }
            else
            {
                _logger.LogError("HttpContext unavailable and AuthenticationStateCache is null for {Method} {Uri}",
                    request.Method, request.RequestUri);
            }
        }

        // Build Cookie header with any available cookies
        var cookies = new List<string>(2);
        if (!string.IsNullOrEmpty(authCookiePrimary)) cookies.Add($"HnH.Auth={authCookiePrimary}");
        if (!string.IsNullOrEmpty(authCookieLegacy)) cookies.Add($"HnHMapper.Auth={authCookieLegacy}");

        if (cookies.Count > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        // Every web user's API call leaves this container from the same address. Forward the browser's origin so
        // the API's per-IP rate limits and audit rows see the user, not the web container. The API trusts XFF
        // only because it is not publicly reachable and Caddy rewrites the header for external traffic.
        if (!string.IsNullOrEmpty(clientIp))
        {
            request.Headers.Remove("X-Forwarded-For");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
            if (!string.IsNullOrEmpty(clientScheme))
            {
                request.Headers.Remove("X-Forwarded-Proto");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", clientScheme);
            }
        }

        // Only log non-SSE requests at Info to reduce noise
        bool isSseRequest = request.RequestUri?.PathAndQuery?.Contains("/map/updates") ?? false;
        if (!isSseRequest)
        {
            _logger.LogInformation("Outgoing API request {Method} {Uri} (HasAuthCookie: {HasCookie}, UsingCache: {UsingCache})",
                request.Method, request.RequestUri, cookies.Count > 0, usingCache);
        }

        // Send the request
        var response = await base.SendAsync(request, cancellationToken);

        // Log authentication failures with detailed cookie info for debugging
        if (response.StatusCode == HttpStatusCode.Unauthorized || (int)response.StatusCode == 302)
        {
            _logger.LogWarning("API auth failure {Status} from {Method} {Uri} | HadCookie: {HasCookie}, UsingCache: {UsingCache}, CookieSource: {CookieSource}",
                response.StatusCode, request.Method, request.RequestUri, cookies.Count > 0, usingCache, 
                httpContext != null ? "HttpContext" : (_authStateCache?.CookieValue != null ? "AuthStateCache" : "None"));
        }

        return response;
    }
}