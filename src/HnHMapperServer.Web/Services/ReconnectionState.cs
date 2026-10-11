namespace HnHMapperServer.Web.Services;

/// <summary>
/// Per-circuit (scoped) record of whether the circuit's SignalR connection is up, kept by
/// ReconnectionCircuitHandler. The framework runs the circuit handlers before it disposes components, so during
/// circuit teardown IsConnected is already false: never start JS interop while it is false (the call cannot be
/// delivered and is not replayed after a reconnect). Components also subscribe to OnDown/OnUp to pause work.
/// </summary>
public class ReconnectionState
{
    private bool _isConnected = true;
    private readonly object _lock = new();

    /// <summary>
    /// Current connection status
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _isConnected;
            }
        }
        private set
        {
            lock (_lock)
            {
                _isConnected = value;
            }
        }
    }

    /// <summary>
    /// Event fired when circuit goes down (disconnected)
    /// </summary>
    public event Action? OnDown;

    /// <summary>
    /// Event fired when circuit comes back up (reconnected)
    /// </summary>
    public event Action? OnUp;

    /// <summary>
    /// Mark circuit as disconnected and notify subscribers
    /// </summary>
    public void MarkDisconnected()
    {
        bool wasConnected;
        lock (_lock)
        {
            wasConnected = _isConnected;
            _isConnected = false;
        }

        // Only fire event if state actually changed
        if (wasConnected)
        {
            OnDown?.Invoke();
        }
    }

    /// <summary>
    /// Mark circuit as reconnected and notify subscribers
    /// </summary>
    public void MarkConnected()
    {
        bool wasDisconnected;
        lock (_lock)
        {
            wasDisconnected = !_isConnected;
            _isConnected = true;
        }

        // Only fire event if state actually changed
        if (wasDisconnected)
        {
            OnUp?.Invoke();
        }
    }
}

