using Microsoft.AspNetCore.Components.Server.Circuits;
using HnHMapperServer.Web.Services;

namespace HnHMapperServer.Web.Security;

/// <summary>
/// Circuit handler that tracks SignalR connection state and notifies ReconnectionState service.
/// Allows components to pause/resume operations during reconnects.
/// </summary>
public class ReconnectionCircuitHandler : CircuitHandler
{
    private readonly ReconnectionState _state;
    private readonly ILogger<ReconnectionCircuitHandler> _logger;

    public ReconnectionCircuitHandler(ReconnectionState state, ILogger<ReconnectionCircuitHandler> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Called when a new circuit is opened (initial connection)
    /// </summary>
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit {CircuitId} opened successfully", circuit.Id);
        _state.MarkConnected();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the circuit connection is lost
    /// </summary>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit {CircuitId} connection down - client disconnected or connection lost", circuit.Id);
        _state.MarkDisconnected();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the circuit reconnects successfully
    /// </summary>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit {CircuitId} connection restored - client reconnected", circuit.Id);
        _state.MarkConnected();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a circuit is closed (disposed)
    /// </summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit {CircuitId} closed permanently", circuit.Id);
        _state.MarkDisconnected();
        return Task.CompletedTask;
    }
}

