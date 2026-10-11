using Bunit;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Web.Components.Map;
using HnHMapperServer.Web.Models;
using HnHMapperServer.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;

namespace HnHMapperServer.Web.Tests;

/// <summary>
/// MapView's one-attempt map initialization: how each way initializeMap can end is logged and reported to the
/// parent (a hidden tab can hang initializeMap until the 2-minute interop timeout, and every successful init
/// used to send a second, unguarded changeMap).
/// </summary>
public sealed class MapViewInitializationTests : IAsyncLifetime
{
    private const int MapId = 42;

    private readonly BunitContext _ctx = new();
    private readonly TestLogger<MapView> _logger = new();
    private readonly List<bool> _readyCalls = [];
    private readonly ReconnectionState _connection = new();
    private BunitJSModuleInterop _module = default!;
    private JSRuntimeInvocationHandler _init = default!;
    private JSRuntimeInvocationHandler _changeMap = default!;

    public Task InitializeAsync()
    {
        var buildInfo = new Mock<IBuildInfoProvider>();
        buildInfo.Setup(b => b.Get(It.IsAny<string>())).Returns(new BuildInfo { Commit = "test" });

        _ctx.Services.AddSingleton(buildInfo.Object);
        _ctx.Services.AddSingleton(_connection);
        _ctx.Services.AddSingleton<SafeJsInterop>();
        _ctx.Services.AddSingleton<ILogger<MapView>>(_logger);

        _module = _ctx.JSInterop.SetupModule("./js/leaflet-interop.js?v=test");
        _init = _module.SetupVoid("initializeMap", _ => true);
        _changeMap = _module.SetupVoid("changeMap", _ => true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private IRenderedComponent<MapView> Render() =>
        _ctx.Render<MapView>(p => p
            .Add(x => x.State, new MapState { CurrentMapId = MapId })
            .Add(x => x.OnMapInitialized, (bool ready) => _readyCalls.Add(ready)));

    private int ChangeMapCalls => _module.Invocations["changeMap"].Count;

    private IEnumerable<LogEntry> WarningsAndErrors => _logger.Entries.Where(e => e.Level >= LogLevel.Warning);

    /// <summary>
    /// JS results complete on the test thread; MapView's continuation then runs on the renderer's dispatcher.
    /// Nothing re-renders, so wait on the observable outcome instead of a render.
    /// </summary>
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 10 s");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Ready_mid_init_sends_changeMap_exactly_once()
    {
        // The usual order: Leaflet 'load' (OnMapReady) arrives before the changeMap reply
        var cut = Render();
        _init.SetVoidResult();
        await cut.InvokeAsync(() => cut.Instance.OnMapReady());
        _changeMap.SetVoidResult();

        await Eventually(() => _logger.Entries.Any(e => e.Message.Contains("initializeMap took")));
        Assert.Equal(1, ChangeMapCalls);
        Assert.Equal(MapId, _module.Invocations["changeMap"][0].Arguments[0]);
        Assert.True(cut.Instance.IsInitialized);
        Assert.Equal(new[] { true }, _readyCalls);
        Assert.Empty(WarningsAndErrors);
    }

    [Fact]
    public async Task Timeout_is_one_warning_and_reports_not_ready()
    {
        var cut = Render();
        _init.SetCanceled();

        await Eventually(() => _readyCalls.Count > 0);
        Assert.Equal(new[] { false }, _readyCalls);
        var warning = Assert.Single(WarningsAndErrors);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<TaskCanceledException>(warning.Exception);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.StartsWith("Stack trace"));
        Assert.Equal(0, ChangeMapCalls);
        Assert.False(cut.Instance.IsInitialized);
    }

    [Fact]
    public async Task JSException_is_one_error_and_reports_not_ready()
    {
        Render();
        _init.SetException(new JSException("boom"));

        await Eventually(() => _readyCalls.Count > 0);
        Assert.Equal(new[] { false }, _readyCalls);
        var error = Assert.Single(WarningsAndErrors);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal("boom", Assert.IsType<JSException>(error.Exception).Message);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.StartsWith("Stack trace"));
        Assert.Equal(0, ChangeMapCalls);
    }

    [Fact]
    public async Task Disposed_while_pending_then_completed_sends_nothing_and_logs_no_failure()
    {
        Render();
        await _ctx.DisposeComponentsAsync();
        _init.SetVoidResult();

        await Eventually(() => _logger.Entries.Any(e => e.Message.Contains("finished after the component was disposed")));

        Assert.Equal(0, ChangeMapCalls);
        Assert.Empty(WarningsAndErrors);
        Assert.Empty(_readyCalls);
    }

    [Fact]
    public async Task Disposed_while_pending_then_timed_out_is_debug_only()
    {
        Render();
        await _ctx.DisposeComponentsAsync();
        _init.SetCanceled();

        await Eventually(() => _logger.Entries.Any(e => e.Message.StartsWith("Map initialization interrupted")));

        Assert.Empty(WarningsAndErrors);
        Assert.Empty(_readyCalls);
    }

    [Fact]
    public async Task Ready_after_dispose_does_not_start_parent_initialization()
    {
        // DisposeAsync keeps dotnetRef alive until its module-dispose round trip ends, so Leaflet 'load' can
        // still arrive. Map's init sequence would then open a live-update stream bound to a dead page.
        var cut = Render();
        _init.SetVoidResult();
        _changeMap.SetVoidResult();
        await Eventually(() => _logger.Entries.Any(e => e.Message.Contains("initializeMap took")));
        var view = cut.Instance; // bUnit refuses Instance once the component is disposed; JS still holds the ref
        await _ctx.DisposeComponentsAsync();

        await cut.InvokeAsync(() => view.OnMapReady());

        Assert.Empty(_readyCalls);
        Assert.False(view.IsInitialized);
        Assert.Contains(_logger.AtLevel(LogLevel.Debug), e => e.Message.Contains("arrived after the component was disposed"));
        Assert.Empty(WarningsAndErrors);
    }

    [Fact]
    public async Task Circuit_gone_is_debug_only()
    {
        Render();
        _init.SetException(new JSDisconnectedException("gone"));

        await Eventually(() => _logger.AtLevel(LogLevel.Debug).Any(e => e.Exception is JSDisconnectedException));
        Assert.Empty(WarningsAndErrors);
        Assert.Empty(_readyCalls);
    }

    [Fact]
    public async Task ChangeMap_reply_lost_after_ready_does_not_report_failure()
    {
        var cut = Render();
        _init.SetVoidResult();
        await cut.InvokeAsync(() => cut.Instance.OnMapReady());
        _changeMap.SetCanceled();

        await Eventually(() => _logger.Entries.Any(e => e.Message.StartsWith("Map initialization interrupted")));
        Assert.Empty(WarningsAndErrors);
        Assert.Equal(new[] { true }, _readyCalls);
        Assert.True(cut.Instance.IsInitialized);
    }

    [Fact]
    public async Task Disconnected_circuit_skips_js_teardown_on_dispose()
    {
        var cut = Render();
        _init.SetVoidResult();
        await cut.InvokeAsync(() => cut.Instance.OnMapReady());
        _changeMap.SetVoidResult();
        await Eventually(() => _logger.Entries.Any(e => e.Message.Contains("initializeMap took")));
        _connection.MarkDisconnected();

        await _ctx.DisposeComponentsAsync();

        Assert.Contains(_logger.AtLevel(LogLevel.Debug), e => e.Message.Contains("skipping JS teardown"));
        Assert.Empty(WarningsAndErrors);
    }
}
