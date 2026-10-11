using System.Net;
using System.Security.Claims;
using HnHMapperServer.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Web.Tests;

/// <summary>
/// The Web→API handler runs in IHttpClientFactory's handler scope, shared by every user for the handler
/// lifetime. These tests drive it through a real factory pipeline (one pooled handler for the whole test), with
/// each scenario in its own Task.Run so the AsyncLocal-backed HttpContext / circuit services cannot leak between
/// scenarios - exactly the separation production gets from separate requests and circuits.
/// </summary>
public sealed class AuthenticationDelegatingHandlerTests : IDisposable
{
    private const string IpA = "203.0.113.7";
    private const string IpB = "198.51.100.23";

    private readonly RecordingHandler _recorder = new();
    private readonly TestLogger<AuthenticationDelegatingHandler> _logger = new();
    private readonly ServiceProvider _root;

    public AuthenticationDelegatingHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILogger<AuthenticationDelegatingHandler>>(_logger);
        services.AddHttpContextAccessor();
        services.AddScoped<AuthenticationStateCache>();
        services.AddSingleton<CircuitServicesAccessor>();
        services.AddTransient<AuthenticationDelegatingHandler>();
        services.AddHttpClient("API", c => c.BaseAddress = new Uri("http://api"))
            .ConfigurePrimaryHttpMessageHandler(() => _recorder)
            .AddHttpMessageHandler<AuthenticationDelegatingHandler>();
        _root = services.BuildServiceProvider();
    }

    public void Dispose() => _root.Dispose();

    private IHttpContextAccessor HttpContextAccessor => _root.GetRequiredService<IHttpContextAccessor>();
    private CircuitServicesAccessor Accessor => _root.GetRequiredService<CircuitServicesAccessor>();

    [Fact]
    public async Task Circuit_cookie_and_origin_are_used_without_HttpContext()
    {
        using var circuit = NewCircuitScope("cookie-A", IpA);

        var sent = await Task.Run(async () =>
        {
            Accessor.Set(new CircuitServicesHolder(circuit.ServiceProvider));
            return await SendAsync();
        });

        Assert.Equal("HnH.Auth=cookie-A", sent.Cookie);
        Assert.Equal(IpA, sent.ForwardedFor);
        Assert.Equal("https", sent.ForwardedProto);
        Assert.Empty(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Timer_created_in_circuit_activity_keeps_auth_after_connection_request_ends()
    {
        // Scenario: the circuit reconnected, the request it started on ended, and the marker timer created during
        // component init lost its HttpContext for good.
        using var circuit = _root.CreateScope();
        var circuitServices = new ServicesAccessorCircuitHandler(circuit.ServiceProvider, Accessor);
        var capture = new AuthenticationCaptureCircuitHandler(HttpContextAccessor,
            circuit.ServiceProvider.GetRequiredService<AuthenticationStateCache>(),
            new TestLogger<AuthenticationCaptureCircuitHandler>());

        var (hadHttpContext, sent) = await Task.Run(async () =>
        {
            // This flow plays the circuit's WebSocket request
            var connectionRequest = NewHttpContext("cookie-A", IpA);
            HttpContextAccessor.HttpContext = connectionRequest;
            Assert.True(capture.Capture(connectionRequest));

            var done = new TaskCompletionSource<(bool, Sent)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Timer? timer = null;
            var activity = circuitServices.CreateInboundActivityHandler(_ =>
            {
                // Component init: the timer captures this execution context
                timer = new Timer(async _ =>
                {
                    try
                    {
                        var stillHasContext = HttpContextAccessor.HttpContext != null;
                        done.TrySetResult((stillHasContext, await SendAsync()));
                    }
                    catch (Exception ex)
                    {
                        done.TrySetException(ex);
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);
                return Task.CompletedTask;
            });
            await activity(null!);

            // The request ends (DefaultHttpContextFactory.Dispose): the holder every descendant context shares is cleared
            HttpContextAccessor.HttpContext = null;

            timer!.Change(0, Timeout.Infinite);
            var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await timer.DisposeAsync();
            return result;
        });

        // Precondition: the HttpContext branch cannot be what passes this test
        Assert.False(hadHttpContext);
        Assert.Equal("HnH.Auth=cookie-A", sent.Cookie);
        Assert.Equal(IpA, sent.ForwardedFor);
        Assert.Empty(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Never_sends_another_users_cookie_or_ip_on_the_same_pooled_handler()
    {
        using var circuitB = NewCircuitScope(cookie: null, IpB);

        // (a) user A's request (prerender/SSR) goes through the pooled handler first
        var sentA = await Task.Run(async () =>
        {
            HttpContextAccessor.HttpContext = NewHttpContext("cookie-A", IpA);
            return await SendAsync();
        });

        // (b) anonymous circuit B's background call
        var sentB = await Task.Run(async () =>
        {
            Accessor.Set(new CircuitServicesHolder(circuitB.ServiceProvider));
            return await SendAsync();
        });

        // (c) a flow with no identity at all
        var sentNone = await Task.Run(SendAsync);

        Assert.Equal("HnH.Auth=cookie-A", sentA.Cookie);
        Assert.Equal(IpA, sentA.ForwardedFor);

        Assert.Null(sentB.Cookie);
        Assert.Equal(IpB, sentB.ForwardedFor);

        Assert.Null(sentNone.Cookie);
        Assert.Null(sentNone.ForwardedFor);
        Assert.Null(sentNone.ForwardedProto);
    }

    [Fact]
    public async Task Anonymous_circuit_forwards_origin_without_warning()
    {
        using var circuit = NewCircuitScope(cookie: null, IpB);

        var sent = await Task.Run(async () =>
        {
            Accessor.Set(new CircuitServicesHolder(circuit.ServiceProvider));
            return await SendAsync();
        });

        Assert.Null(sent.Cookie);
        Assert.Equal(IpB, sent.ForwardedFor);
        Assert.Equal("https", sent.ForwardedProto);
        Assert.Empty(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Plain_request_forwards_only_the_auth_cookie_and_replaces_caller_headers()
    {
        var sent = await Task.Run(async () =>
        {
            HttpContextAccessor.HttpContext = NewHttpContext("cookie-A", IpA);
            var request = new HttpRequestMessage(HttpMethod.Get, "/map/api/config");
            request.Headers.TryAddWithoutValidation("Cookie", "x=1");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.1");
            return await SendAsync(request);
        });

        // NewHttpContext also sends an unrelated cookie; only HnH.Auth is forwarded
        Assert.Equal("HnH.Auth=cookie-A", sent.Cookie);
        Assert.Equal(IpA, sent.ForwardedFor);
        Assert.Equal("https", sent.ForwardedProto);
        Assert.Empty(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task No_circuit_and_no_HttpContext_sends_nothing_and_warns_once_even_on_401()
    {
        _recorder.Status = HttpStatusCode.Unauthorized;

        var sent = await Task.Run(SendAsync);

        Assert.Null(sent.Cookie);
        Assert.Null(sent.ForwardedFor);
        Assert.Single(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Auth_failure_with_a_source_logs_one_warning()
    {
        _recorder.Status = HttpStatusCode.Unauthorized;
        using var circuit = NewCircuitScope("expired", IpA);

        await Task.Run(async () =>
        {
            Accessor.Set(new CircuitServicesHolder(circuit.ServiceProvider));
            return await SendAsync();
        });

        var warning = Assert.Single(_logger.AtLevel(LogLevel.Warning));
        Assert.Contains("Circuit", warning.Message);
    }

    [Fact]
    public async Task Closed_circuit_sends_no_cookie_and_does_not_throw()
    {
        var circuit = NewCircuitScope("cookie-A", IpA);
        var circuitServices = new ServicesAccessorCircuitHandler(circuit.ServiceProvider, Accessor);

        var sent = await Task.Run(async () =>
        {
            var done = new TaskCompletionSource<Sent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Timer? timer = null;
            await circuitServices.CreateInboundActivityHandler(_ =>
            {
                timer = new Timer(async _ =>
                {
                    try { done.TrySetResult(await SendAsync()); }
                    catch (Exception ex) { done.TrySetException(ex); }
                }, null, Timeout.Infinite, Timeout.Infinite);
                return Task.CompletedTask;
            })(null!);

            await circuitServices.OnCircuitClosedAsync(null!, CancellationToken.None);
            circuit.Dispose();

            timer!.Change(0, Timeout.Infinite);
            var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await timer.DisposeAsync();
            return result;
        });

        Assert.Null(sent.Cookie);
        Assert.Null(sent.ForwardedFor);
        Assert.Single(_logger.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Disposed_circuit_scope_without_cleared_holder_sends_nothing_and_does_not_throw()
    {
        var circuit = NewCircuitScope("cookie-A", IpA);
        var holder = new CircuitServicesHolder(circuit.ServiceProvider);
        circuit.Dispose();

        var sent = await Task.Run(async () =>
        {
            Accessor.Set(holder);
            return await SendAsync();
        });

        Assert.Null(sent.Cookie);
        Assert.Null(sent.ForwardedFor);
    }

    private IServiceScope NewCircuitScope(string? cookie, string ip)
    {
        var scope = _root.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<AuthenticationStateCache>();
        cache.CookieValue = cookie;
        cache.ClientOrigin = (ip, "https");
        return scope;
    }

    private static DefaultHttpContext NewHttpContext(string cookie, string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers.Cookie = $"other=1; HnH.Auth={cookie}";
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "player")], "test"));
        return context;
    }

    private Task<Sent> SendAsync() => SendAsync(new HttpRequestMessage(HttpMethod.Get, "/map/api/v1/markers"));

    private async Task<Sent> SendAsync(HttpRequestMessage request)
    {
        var client = _root.GetRequiredService<IHttpClientFactory>().CreateClient("API");
        using var response = await client.SendAsync(request);
        var sentRequest = response.RequestMessage!;
        return new Sent(
            Header(sentRequest, "Cookie"),
            Header(sentRequest, "X-Forwarded-For"),
            Header(sentRequest, "X-Forwarded-Proto"));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

    private sealed record Sent(string? Cookie, string? ForwardedFor, string? ForwardedProto);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status) { RequestMessage = request });
    }
}
