using HnHMapperServer.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;

namespace HnHMapperServer.Web.Tests;

public sealed class SafeJsInteropTests
{
    private readonly ReconnectionState _state = new();
    private readonly Mock<IJSRuntime> _runtime = new();
    private readonly Mock<IJSObjectReference> _module = new();
    private readonly TestLogger<SafeJsInterop> _logger = new();

    private SafeJsInterop CreateSut() => new(_logger, _runtime.Object, _state);

    [Fact]
    public async Task Disconnected_circuit_skips_the_call_without_touching_js()
    {
        _state.MarkDisconnected();

        Assert.False(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        Assert.Empty(_module.Invocations);
        Assert.Empty(_runtime.Invocations);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task Null_module_returns_false_without_a_runtime_call()
    {
        Assert.False(await CreateSut().InvokeVoidSafeAsync(null, "changeMap", 1));

        // The old code reported this through console.error - a JS call from the failure path
        Assert.Empty(_runtime.Invocations);
    }

    [Fact]
    public async Task Connected_call_reaches_the_module_once()
    {
        SetupVoid("changeMap", ValueTask.FromResult<IJSVoidResult>(null!));

        Assert.True(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        Assert.Single(_module.Invocations);
        Assert.Empty(_runtime.Invocations);
    }

    [Fact]
    public async Task JSDisconnected_returns_false_without_a_second_js_call()
    {
        SetupVoid("changeMap", ValueTask.FromException<IJSVoidResult>(new JSDisconnectedException("gone")));

        Assert.False(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        Assert.Empty(_runtime.Invocations);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task Timeout_while_connected_is_a_warning_and_makes_no_second_js_call()
    {
        SetupVoid("changeMap", ValueTask.FromException<IJSVoidResult>(new TaskCanceledException()));

        Assert.False(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        var warning = Assert.Single(_logger.AtLevel(LogLevel.Warning));
        Assert.IsType<TaskCanceledException>(warning.Exception);
        Assert.Empty(_runtime.Invocations);
    }

    [Fact]
    public async Task Timeout_after_the_circuit_went_down_is_debug_only()
    {
        _module
            .Setup(m => m.InvokeAsync<IJSVoidResult>("changeMap", It.IsAny<object?[]?>()))
            .Returns(() =>
            {
                _state.MarkDisconnected();
                return ValueTask.FromException<IJSVoidResult>(new TaskCanceledException());
            });

        Assert.False(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.NotEmpty(_logger.AtLevel(LogLevel.Debug));
    }

    [Fact]
    public async Task Unexpected_exception_is_a_warning()
    {
        SetupVoid("changeMap", ValueTask.FromException<IJSVoidResult>(new JSException("boom")));

        Assert.False(await CreateSut().InvokeVoidSafeAsync(_module.Object, "changeMap", 1));

        Assert.IsType<JSException>(Assert.Single(_logger.AtLevel(LogLevel.Warning)).Exception);
    }

    [Fact]
    public async Task Value_and_local_storage_calls_skip_js_while_disconnected()
    {
        _state.MarkDisconnected();
        var sut = CreateSut();

        Assert.Equal(0, await sut.InvokeSafeAsync<int>(_module.Object, "getCount"));
        Assert.Null(await sut.GetLocalStorageAsync("key"));
        Assert.False(await sut.SetLocalStorageAsync("key", "value"));

        Assert.Empty(_module.Invocations);
        Assert.Empty(_runtime.Invocations);
    }

    private void SetupVoid(string identifier, ValueTask<IJSVoidResult> result) =>
        _module
            .Setup(m => m.InvokeAsync<IJSVoidResult>(identifier, It.IsAny<object?[]?>()))
            .Returns(result);
}
