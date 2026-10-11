namespace HnHMapperServer.Web.Services;

/// <summary>
/// Gives code that runs outside the Blazor circuit's DI scope - above all the IHttpClientFactory
/// DelegatingHandlers, which the factory resolves in its own handler scope shared by every user for the
/// handler lifetime - the services of the circuit whose work it is doing. Microsoft's documented
/// "access server-side Blazor services from a different DI scope" pattern.
///
/// The value is an AsyncLocal set by <see cref="ServicesAccessorCircuitHandler"/> around every inbound
/// circuit activity (component init, events, JS interop, navigation, render callbacks). Execution contexts
/// captured inside an activity keep it: a Timer created in OnInitializedAsync, Task.Run, every await
/// continuation. It is therefore NOT available in CircuitHandler callbacks (OnConnectionUpAsync is not an
/// inbound activity) or in anything started from a singleton event source - never create API-calling
/// timers or loops there.
/// </summary>
public sealed class CircuitServicesAccessor
{
    private static readonly AsyncLocal<CircuitServicesHolder?> Current = new();

    /// <summary>The current circuit's service provider; null outside a circuit flow or after the circuit closed.</summary>
    public IServiceProvider? Services => Current.Value?.Services;

    internal void Set(CircuitServicesHolder? holder) => Current.Value = holder;
}

/// <summary>
/// One per circuit. Captured execution contexts (timers, background loops) hold this holder, not the
/// provider itself, so clearing it when the circuit closes releases the circuit scope everywhere at once -
/// a stale timer then resolves nothing instead of a disposed provider.
/// </summary>
public sealed class CircuitServicesHolder(IServiceProvider services)
{
    private IServiceProvider? _services = services;

    public IServiceProvider? Services => Volatile.Read(ref _services);

    public void Clear() => Volatile.Write(ref _services, null);
}
