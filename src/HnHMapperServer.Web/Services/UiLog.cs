using Microsoft.JSInterop;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Log levels for failures caught in Blazor components (see CLAUDE.md "Logging levels").
/// Components log through ILogger, never Console.WriteLine, so these reach the dashboard.
/// </summary>
public static class UiLog
{
    /// <summary>
    /// A failed user action or API call is a Warning. The circuit or JS side going away mid-call
    /// (JSDisconnectedException, a cancellation that is not an HttpClient timeout) is routine: Debug.
    /// </summary>
    public static LogLevel FailureLevel(Exception ex) =>
        ex is JSDisconnectedException or OperationCanceledException { InnerException: not TimeoutException }
            ? LogLevel.Debug
            : LogLevel.Warning;
}
