using System.Security.Claims;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Scoped service that caches authentication state for the Blazor circuit lifetime.
/// This allows background threads (like timers) to access authentication information
/// without requiring HttpContext.
///
/// Thread-safe: Uses lock to ensure visibility across threads (main circuit thread and background timers).
/// </summary>
public class AuthenticationStateCache
{
    private readonly object _lock = new();
    private string? _cookieValue;
    private ClaimsPrincipal? _user;
    private DateTime _lastUpdated;
    private string? _clientIp;
    private string? _clientScheme;

    /// <summary>
    /// The browser's address (already de-proxied by the forwarded-headers middleware) and scheme, captured
    /// while an HttpContext is available so circuit/background requests to the API can still carry them.
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
