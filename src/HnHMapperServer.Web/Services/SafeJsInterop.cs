using Microsoft.JSInterop;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// JS interop wrappers that never throw. While the circuit is disconnected
/// (<see cref="ReconnectionState.IsConnected"/> false) they do not call JS at all: a call sent into a dead
/// connection is never delivered and Blazor does not replay it after a reconnect, so it could only end as
/// a TaskCanceledException after JSInteropDefaultCallTimeout. Failures are logged and turned into
/// false/default; they are never reported through another JS call (that would be a second call into the
/// same broken connection).
/// </summary>
public class SafeJsInterop
{
    private readonly ILogger<SafeJsInterop> _logger;
    private readonly IJSRuntime _jsRuntime;
    private readonly ReconnectionState _connection;

    public SafeJsInterop(ILogger<SafeJsInterop> logger, IJSRuntime jsRuntime, ReconnectionState connection)
    {
        _logger = logger;
        _jsRuntime = jsRuntime;
        _connection = connection;
    }

    /// <summary>
    /// Safely invoke a void JS method.
    /// </summary>
    /// <param name="module">JS module reference</param>
    /// <param name="identifier">JS function name</param>
    /// <param name="args">Function arguments</param>
    /// <returns>True if the call completed; false if it was skipped or failed</returns>
    public async Task<bool> InvokeVoidSafeAsync(IJSObjectReference? module, string identifier, params object?[]? args)
    {
        if (module == null)
        {
            _logger.LogDebug("InvokeVoidSafe: module is null for {Identifier}", identifier);
            return false;
        }

        if (!CanCallJs("InvokeVoidSafe", identifier))
        {
            return false;
        }

        try
        {
            await module.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "InvokeVoidSafe", identifier);
            return false;
        }
    }

    /// <summary>
    /// Safely invoke a JS method that returns a value.
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="module">JS module reference</param>
    /// <param name="identifier">JS function name</param>
    /// <param name="args">Function arguments</param>
    /// <returns>Result value, or default(T) if the call was skipped or failed</returns>
    public async Task<T?> InvokeSafeAsync<T>(IJSObjectReference? module, string identifier, params object?[]? args)
    {
        if (module == null)
        {
            _logger.LogDebug("InvokeSafe: module is null for {Identifier}", identifier);
            return default;
        }

        if (!CanCallJs("InvokeSafe", identifier))
        {
            return default;
        }

        try
        {
            return await module.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "InvokeSafe", identifier);
            return default;
        }
    }

    /// <summary>
    /// Safely get a value from localStorage.
    /// </summary>
    /// <param name="key">localStorage key</param>
    /// <returns>Value, or null if not found, skipped or failed</returns>
    public async Task<string?> GetLocalStorageAsync(string key)
    {
        if (!CanCallJs("GetLocalStorage", key))
        {
            return null;
        }

        try
        {
            return await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "GetLocalStorage", key);
            return null;
        }
    }

    /// <summary>
    /// Safely set a value in localStorage.
    /// </summary>
    /// <param name="key">localStorage key</param>
    /// <param name="value">Value to store</param>
    /// <returns>True if stored; false if skipped or failed</returns>
    public async Task<bool> SetLocalStorageAsync(string key, string value)
    {
        if (!CanCallJs("SetLocalStorage", key))
        {
            return false;
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, value);
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "SetLocalStorage", key);
            return false;
        }
    }

    private bool CanCallJs(string operation, string target)
    {
        if (_connection.IsConnected)
        {
            return true;
        }

        _logger.LogDebug("{Operation}: skipped {Target} - circuit not connected", operation, target);
        return false;
    }

    private void LogFailure(Exception ex, string operation, string target)
    {
        switch (ex)
        {
            case OperationCanceledException when _connection.IsConnected:
                // No answer within JSInteropDefaultCallTimeout although the circuit is up: a hung JS call,
                // or one lost with a connection that dropped and came back meanwhile.
                _logger.LogWarning(ex, "{Operation}: {Target} timed out while the circuit is connected (JS hang or call lost across a reconnect)",
                    operation, target);
                break;
            case OperationCanceledException or JSDisconnectedException or ObjectDisposedException:
                _logger.LogDebug("{Operation}: {Target} abandoned ({ExceptionType})", operation, target, ex.GetType().Name);
                break;
            default:
                _logger.LogWarning(ex, "{Operation}: unexpected exception for {Target}", operation, target);
                break;
        }
    }
}
