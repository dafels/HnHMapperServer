using System.Security.Claims;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Scoped service that caches the circuit's authentication state for the circuit lifetime, filled by
/// <see cref="AuthenticationCaptureCircuitHandler"/> on every (re)connect. Circuit work that outlives the
/// connection's request (timers, loops) reaches it through <see cref="CircuitServicesAccessor"/>.
/// Never inject it into an IHttpClientFactory DelegatingHandler: those are resolved in the factory's own
/// handler scope, so the instance they get is shared by every user of the pooled handler.
///
/// Thread-safe: Uses lock to ensure visibility across threads (main circuit thread and background timers).
/// </summary>
public class AuthenticationStateCache
{
    private readonly System.Threading.Lock _lock = new();
    private string? _cookieValue;
    private ClaimsPrincipal? _user;
    private DateTime _lastUpdated;
    private string? _clientIp;
    private string? _clientScheme;

    /// <summary>
    /// The browser's address (already de-proxied by the forwarded-headers middleware) and scheme, captured
    /// while the circuit's HttpContext is available so circuit/background requests to the API can still carry them.
    /// </summary>
    public (string? Ip, string? Scheme) ClientOrigin
    {
        get
        {
            lock (_lock)
            {
                return (_clientIp, _clientScheme);
            }
        }
        set
        {
            lock (_lock)
            {
                _clientIp = value.Ip;
                _clientScheme = value.Scheme;
            }
        }
    }

    public string? CookieValue
    {
        get
        {
            lock (_lock)
            {
                return _cookieValue;
            }
        }
        set
        {
            lock (_lock)
            {
                _cookieValue = value;
                _lastUpdated = DateTime.UtcNow;
            }
        }
    }

    public ClaimsPrincipal? User
    {
        get
        {
            lock (_lock)
            {
                return _user;
            }
        }
        set
        {
            lock (_lock)
            {
                _user = value;
                _lastUpdated = DateTime.UtcNow;
            }
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_lock)
            {
                return _user?.Identity?.IsAuthenticated == true;
            }
        }
    }

    public string? Username
    {
        get
        {
            lock (_lock)
            {
                return _user?.Identity?.Name;
            }
        }
    }

    /// <summary>
    /// Gets when the cache was last updated (for diagnostic purposes)
    /// </summary>
    public DateTime LastUpdated
    {
        get
        {
            lock (_lock)
            {
                return _lastUpdated;
            }
        }
    }

    /// <summary>
    /// Gets authentication state in a single atomic operation (thread-safe snapshot)
    /// </summary>
    public (bool IsAuthenticated, string? CookieValue, string? Username) GetSnapshot()
    {
        lock (_lock)
        {
            return (
                _user?.Identity?.IsAuthenticated == true,
                _cookieValue,
                _user?.Identity?.Name
            );
        }
    }
}
