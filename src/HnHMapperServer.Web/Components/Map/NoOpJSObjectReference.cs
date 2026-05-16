using Microsoft.JSInterop;

namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// A no-op IJSObjectReference used in native-map mode so that the page's existing
/// Leaflet interop call sites don't NRE. Returns sensible defaults: true for bool
/// (treats every "did the toggle apply?" check as success so UI flips state),
/// default for everything else.
/// </summary>
internal sealed class NoOpJSObjectReference : IJSObjectReference
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => ValueTask.FromResult(DefaultFor<TValue>());

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => ValueTask.FromResult(DefaultFor<TValue>());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TValue DefaultFor<TValue>()
    {
        if (typeof(TValue) == typeof(bool)) return (TValue)(object)true;
        return default!;
    }
}
