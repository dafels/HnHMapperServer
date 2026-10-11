namespace HnHMapperServer.Web.Services.Map;

/// <summary>
/// Scheduling policy for the /map game-marker poll. The poll never stops: after a failure it retries
/// after 2, 4 and 8 seconds, then every <see cref="Interval"/> for as long as the failure lasts, and the
/// next success returns it to the normal interval. The player is told once per failure streak (when the
/// fast retries are used up) and once when it recovers. Thread-safe: timer ticks and reconnect handling
/// run on different threads.
/// </summary>
public sealed class MarkerPollBackoff
{
    /// <summary>The normal poll interval, and the retry interval once the fast retries are used up.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan[] FastRetries =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    private readonly Lock _lock = new();
    private int _failures;
    private bool _notified;

    /// <summary>When to poll next, and whether to tell the player that updates are failing / have resumed.</summary>
    public readonly record struct Decision(TimeSpan Delay, bool NotifyFailing, bool NotifyRecovered);

    public Decision OnSuccess()
    {
        lock (_lock)
        {
            var recovered = _notified;
            _failures = 0;
            _notified = false;
            return new Decision(Interval, NotifyFailing: false, NotifyRecovered: recovered);
        }
    }

    public Decision OnFailure()
    {
        lock (_lock)
        {
            if (_failures < FastRetries.Length)
            {
                return new Decision(FastRetries[_failures++], NotifyFailing: false, NotifyRecovered: false);
            }

            // Capped so the counter cannot overflow on a tab left failing for years
            _failures = FastRetries.Length + 1;
            var notify = !_notified;
            _notified = true;
            return new Decision(Interval, NotifyFailing: notify, NotifyRecovered: false);
        }
    }

    /// <summary>
    /// After a circuit reconnect: the cause of the streak may be gone, so start over with the fast retries.
    /// The "failing" notice already shown stays shown, so a later success still reports "resumed" and a
    /// still-failing poll does not repeat the notice.
    /// </summary>
    public void ResetFastRetries()
    {
        lock (_lock)
        {
            _failures = 0;
        }
    }
}
