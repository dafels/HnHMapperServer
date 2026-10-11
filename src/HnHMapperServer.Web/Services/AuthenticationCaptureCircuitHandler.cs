using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Captures the circuit's own authentication cookie and browser origin into its scoped
/// <see cref="AuthenticationStateCache"/> whenever the circuit (re)connects, while the SignalR request's
/// HttpContext is available. <see cref="AuthenticationDelegatingHandler"/> reads that cache through
/// <see cref="CircuitServicesAccessor"/>, so circuit work keeps its identity after the connection's request is
/// gone (timer ticks after a reconnect).
/// </summary>
public sealed class AuthenticationCaptureCircuitHandler : CircuitHandler
{
    internal const string AuthCookieName = "HnH.Auth";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationStateCache _authStateCache;
    private readonly ILogger<AuthenticationCaptureCircuitHandler> _logger;

    public AuthenticationCaptureCircuitHandler(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationStateCache authStateCache,
        ILogger<AuthenticationCaptureCircuitHandler> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _authStateCache = authStateCache;
        _logger = logger;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!Capture(_httpContextAccessor.HttpContext))
        {
            _logger.LogWarning("No HttpContext when circuit {CircuitId} connected; keeping the previously captured auth state",
                circuit?.Id);
        }

        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _authStateCache.User = null;
        _authStateCache.CookieValue = null;
        _authStateCache.ClientOrigin = (null, null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies user, auth cookie and origin from <paramref name="httpContext"/>. Returns false and keeps the
    /// previous values when there is no HttpContext - a reconnect must not wipe a working identity. A request
    /// without the cookie DOES clear it (signed out, then reconnected).
    /// </summary>
    internal bool Capture(HttpContext? httpContext)
    {
        if (httpContext == null)
        {
            return false;
        }

        var user = httpContext.User;
        var cookie = httpContext.Request.Cookies[AuthCookieName];

        _authStateCache.User = user;
        _authStateCache.CookieValue = string.IsNullOrEmpty(cookie) ? null : cookie;
        _authStateCache.ClientOrigin = (httpContext.Connection.RemoteIpAddress?.ToString(), httpContext.Request.Scheme);

        if (_authStateCache.CookieValue == null && user?.Identity?.IsAuthenticated == true)
        {
            // Authenticated by the Web's cookie handler yet no HnH.Auth cookie to forward: every API call
            // from this circuit will be a 401.
            _logger.LogWarning("Authenticated circuit connected without an HnH.Auth cookie; its API calls will be unauthenticated");
        }
        else
        {
            _logger.LogDebug("Captured circuit auth state (authenticated: {IsAuthenticated}, cookie: {HasCookie})",
                user?.Identity?.IsAuthenticated == true, _authStateCache.CookieValue != null);
        }

        return true;
    }
}
