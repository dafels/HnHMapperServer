using System.Net;
using System.Security.Claims;
using HnHMapperServer.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Web.Tests;

public sealed class AuthenticationCaptureCircuitHandlerTests
{
    private readonly AuthenticationStateCache _cache = new();
    private readonly TestLogger<AuthenticationCaptureCircuitHandler> _logger = new();
    private readonly HttpContextAccessor _httpContextAccessor = new();

    private AuthenticationCaptureCircuitHandler CreateHandler() => new(_httpContextAccessor, _cache, _logger);

    [Fact]
    public void Capture_sets_user_cookie_and_client_origin()
    {
        var context = NewHttpContext("HnH.Auth=cookie-A", authenticated: true);

        Assert.True(CreateHandler().Capture(context));

        Assert.Same(context.User, _cache.User);
        Assert.Equal("cookie-A", _cache.CookieValue);
        Assert.Equal(("203.0.113.7", "https"), _cache.ClientOrigin);
        Assert.Empty(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public void Capture_without_HttpContext_returns_false_and_keeps_previous_state()
    {
        var handler = CreateHandler();
        handler.Capture(NewHttpContext("HnH.Auth=cookie-A", authenticated: true));

        Assert.False(handler.Capture(null));

        Assert.Equal("cookie-A", _cache.CookieValue);
        Assert.Equal(("203.0.113.7", "https"), _cache.ClientOrigin);
    }

    [Fact]
    public void Capture_with_HttpContext_but_no_cookie_clears_the_cookie()
    {
        // Signed out, then the circuit reconnected
        var handler = CreateHandler();
        handler.Capture(NewHttpContext("HnH.Auth=cookie-A", authenticated: true));

        Assert.True(handler.Capture(NewHttpContext(cookieHeader: null, authenticated: false)));

        Assert.Null(_cache.CookieValue);
        Assert.Equal(("203.0.113.7", "https"), _cache.ClientOrigin);
    }

    [Fact]
    public void Legacy_cookie_name_is_ignored()
    {
        CreateHandler().Capture(NewHttpContext("HnHMapper.Auth=legacy", authenticated: false));

        Assert.Null(_cache.CookieValue);
    }

    [Fact]
    public void Anonymous_circuit_is_logged_at_debug_only()
    {
        CreateHandler().Capture(NewHttpContext(cookieHeader: null, authenticated: false));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Single(_logger.AtLevel(LogLevel.Debug));
    }

    [Fact]
    public void Authenticated_circuit_without_cookie_warns()
    {
        CreateHandler().Capture(NewHttpContext(cookieHeader: null, authenticated: true));

        Assert.Single(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Connection_up_without_HttpContext_warns_and_keeps_state()
    {
        var handler = CreateHandler();
        handler.Capture(NewHttpContext("HnH.Auth=cookie-A", authenticated: true));

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal("cookie-A", _cache.CookieValue);
        Assert.Single(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Circuit_closed_clears_user_cookie_and_origin()
    {
        var handler = CreateHandler();
        handler.Capture(NewHttpContext("HnH.Auth=cookie-A", authenticated: true));

        await handler.OnCircuitClosedAsync(null!, CancellationToken.None);

        Assert.Null(_cache.User);
        Assert.Null(_cache.CookieValue);
        Assert.Equal(((string?)null, (string?)null), _cache.ClientOrigin);
    }

    private static DefaultHttpContext NewHttpContext(string? cookieHeader, bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        if (cookieHeader != null)
        {
            context.Request.Headers.Cookie = cookieHeader;
        }
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "player")], "test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return context;
    }
}
