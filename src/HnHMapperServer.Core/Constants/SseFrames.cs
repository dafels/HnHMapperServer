namespace HnHMapperServer.Core.Constants;

/// <summary>
/// Wire frames shared by the server-sent event streams and their browser clients.
/// </summary>
public static class SseFrames
{
    /// <summary>
    /// Event name of <see cref="Heartbeat"/>. map-updates.js listens for exactly this name.
    /// </summary>
    public const string HeartbeatEvent = "heartbeat";

    /// <summary>
    /// Liveness frame of <c>/map/updates</c>: a NAMED event with a data line.
    /// <para>
    /// It must never become an SSE comment (<c>": keep-alive"</c>): EventSource discards comment lines
    /// without dispatching anything to JavaScript, so the map-updates.js watchdog would see a quiet stream
    /// as dead and reconnect it — which is exactly what the old comment keep-alive caused (every idle map
    /// stream was torn down and reopened about every 17 s). An event without a data line is not dispatched
    /// either.
    /// </para>
    /// <para>
    /// The server sends it once when its live loop starts (that also flushes the response headers) and then
    /// every 5 s, unconditionally — not only when idle, so no future event type can starve the watchdog.
    /// </para>
    /// </summary>
    public const string Heartbeat = "event: " + HeartbeatEvent + "\ndata: {}\n\n";
}
