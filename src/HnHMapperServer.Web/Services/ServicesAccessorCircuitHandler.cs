using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Publishes the circuit's service provider through <see cref="CircuitServicesAccessor"/> for the duration of
/// every inbound circuit activity. On ASP.NET Core 10 the framework wraps root-component operations (first
/// render and component init), JS-to-.NET calls, JS interop completions, navigation and render callbacks in
/// this handler, but not OnConnectionUpAsync/OnConnectionDownAsync.
/// </summary>
public sealed class ServicesAccessorCircuitHandler : CircuitHandler
{
    private readonly CircuitServicesHolder _holder;
    private readonly CircuitServicesAccessor _accessor;

    public ServicesAccessorCircuitHandler(IServiceProvider services, CircuitServicesAccessor accessor)
    {
        _holder = new CircuitServicesHolder(services);
        _accessor = accessor;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            _accessor.Set(_holder);
            try
            {
                await next(context);
            }
            finally
            {
                // Only resets this activity's own flow; contexts captured inside it keep the holder.
                _accessor.Set(null);
            }
        };

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Do not dereference 'circuit' here (tests pass null).
        _holder.Clear();
        return Task.CompletedTask;
    }
}
