using HnHMapperServer.Web.Services;

namespace HnHMapperServer.Web.Tests;

/// <summary>
/// ReconnectionState alone. The framework ordering the disposal gates rely on (circuit handlers run before
/// components are disposed) is not unit-testable here; it is covered by the manual checks in CLAUDE.md.
/// </summary>
public sealed class ReconnectionStateTests
{
    [Fact]
    public void New_state_is_connected_and_marking_connected_does_not_fire_up()
    {
        var state = new ReconnectionState();
        var ups = 0;
        state.OnUp += () => ups++;

        state.MarkConnected();

        Assert.True(state.IsConnected);
        Assert.Equal(0, ups);
    }

    [Fact]
    public void Disconnect_fires_down_once_and_a_repeat_is_a_no_op()
    {
        // OnConnectionDown followed by OnCircuitClosed marks it twice during teardown
        var state = new ReconnectionState();
        var downs = 0;
        state.OnDown += () => downs++;

        state.MarkDisconnected();
        state.MarkDisconnected();

        Assert.False(state.IsConnected);
        Assert.Equal(1, downs);
    }

    [Fact]
    public void Reconnect_fires_up_once_and_restores_connected()
    {
        var state = new ReconnectionState();
        var ups = 0;
        state.OnUp += () => ups++;
        state.MarkDisconnected();

        state.MarkConnected();
        state.MarkConnected();

        Assert.True(state.IsConnected);
        Assert.Equal(1, ups);
    }

    [Fact]
    public void State_is_already_flipped_when_down_handlers_run()
    {
        // Map.HandleCircuitDown and the disposal gates read IsConnected; it must be false by then
        var state = new ReconnectionState();
        bool? seen = null;
        state.OnDown += () => seen = state.IsConnected;

        state.MarkDisconnected();

        Assert.False(seen);
    }
}
