using System.Net;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Forwards the caller's authentication cookie and browser origin on every Web→API request.
///
/// IHttpClientFactory resolves this handler in its own DI scope, shared by every user of the pooled handler
/// for its lifetime, so it is stateless: it never caches or writes anything, and it must never take a
/// scoped/circuit service in its constructor. The identity comes from the flow that sends the request:
/// <list type="number">
///   <item>Circuit work (component code, and timers or loops it started) - the circuit's own
///   <see cref="AuthenticationStateCache"/> through <see cref="CircuitServicesAccessor"/>. Authoritative,
///   including "no cookie" for an anonymous circuit; still valid after a reconnect ended the request the
///   circuit started on.</item>
///   <item>Otherwise the current HTTP request (SSR, prerender, the minimal-API proxies).</item>
///   <item>Otherwise nothing is sent, with a warning.</item>
/// </list>
/// The handler owns the Cookie, X-Forwarded-For and X-Forwarded-Proto headers: caller-set values are dropped.
/// </summary>
public class AuthenticationDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly CircuitServicesAccessor _circuitServices;
    private readonly ILogger<AuthenticationDelegatingHandler> _logger;

    public AuthenticationDelegatingHandler(
        IHttpContextAccessor httpContextAccessor,
        CircuitServicesAccessor circuitServices,
        ILogger<AuthenticationDelegatingHandler> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _circuitServices = circuitServices;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string source;
        string? authCookie;
        string? clientIp;
        string? clientScheme;

        var circuitCache = TryGetCircuitCache();
        if (circuitCache != null)
        {
            source = "Circuit";
            authCookie = circuitCache.CookieValue;
            (clientIp, clientScheme) = circuitCache.ClientOrigin;
        }
        else if (_httpContextAccessor.HttpContext is { } httpContext)
        {
            source = "HttpContext";
            authCookie = httpContext.Request.Cookies[AuthenticationCaptureCircuitHandler.AuthCookieName];
            clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
            clientScheme = httpContext.Request.Scheme;
        }
        else
        {
            source = "None";
            authCookie = null;
            clientIp = null;
            clientScheme = null;
            // The one Warning for this call: a 401 that follows is not logged again below.
            _logger.LogWarning("No circuit services and no HttpContext for {Method} {Uri}; sending without auth cookie",
                request.Method, request.RequestUri);
        }

        request.Headers.Remove("Cookie");
        request.Headers.Remove("X-Forwarded-For");
        request.Headers.Remove("X-Forwarded-Proto");

        if (!string.IsNullOrEmpty(authCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{AuthenticationCaptureCircuitHandler.AuthCookieName}={authCookie}");
        }

        // Every web user's API call leaves this container from the same address. Forward the browser's origin so
        // the API's per-IP rate limits and audit rows see the user, not the web container. The API trusts XFF
        // only because it is not publicly reachable and Caddy rewrites the header for external traffic.
        if (!string.IsNullOrEmpty(clientIp))
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
            if (!string.IsNullOrEmpty(clientScheme))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", clientScheme);
            }
        }

        _logger.LogDebug("Outgoing API request {Method} {Uri} (AuthSource: {AuthSource}, HasAuthCookie: {HasCookie})",
            request.Method, request.RequestUri, source, !string.IsNullOrEmpty(authCookie));

        var response = await base.SendAsync(request, cancellationToken);

        if (source != "None" &&
            (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Found))
        {
            _logger.LogWarning("API auth failure {Status} from {Method} {Uri} (AuthSource: {AuthSource}, HasAuthCookie: {HasCookie})",
                (int)response.StatusCode, request.Method, request.RequestUri, source, !string.IsNullOrEmpty(authCookie));
        }

        return response;
    }

    /// <summary>
    /// The sending circuit's auth cache, or null outside circuit work. A circuit closed under a still-running
    /// timer resolves nothing (its holder is cleared; a disposed scope throws, which means the same).
    /// </summary>
    private AuthenticationStateCache? TryGetCircuitCache()
    {
        try
        {
            return _circuitServices.Services?.GetService<AuthenticationStateCache>();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
