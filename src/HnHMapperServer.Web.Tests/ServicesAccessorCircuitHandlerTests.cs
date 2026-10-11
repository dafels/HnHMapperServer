using HnHMapperServer.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HnHMapperServer.Web.Tests;

public sealed class ServicesAccessorCircuitHandlerTests
{
    [Fact]
    public async Task Activity_sees_circuit_services_and_the_caller_flow_never_does()
    {
        await Task.Run(async () =>
        {
            using var root = new ServiceCollection().BuildServiceProvider();
            using var circuit = root.CreateScope();
            var accessor = new CircuitServicesAccessor();
            var handler = new ServicesAccessorCircuitHandler(circuit.ServiceProvider, accessor);

            IServiceProvider? seenInside = null;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IServiceProvider?>? startedInside = null;

            await handler.CreateInboundActivityHandler(_ =>
            {
                seenInside = accessor.Services;
                // Work started inside the activity that outlives it (a timer, Task.Run, a continuation)
                startedInside = Task.Run(async () =>
                {
                    await release.Task;
                    return accessor.Services;
                });
                return Task.CompletedTask;
            })(null!);

            // Set-without-restore would hand this circuit's identity to unrelated flows
            Assert.Null(accessor.Services);
            Assert.Same(circuit.ServiceProvider, seenInside);

            release.SetResult();
            Assert.Same(circuit.ServiceProvider, await startedInside!);
        });
    }

    [Fact]
    public async Task Closing_the_circuit_releases_its_services_in_captured_flows()
    {
        await Task.Run(async () =>
        {
            using var root = new ServiceCollection().BuildServiceProvider();
            using var circuit = root.CreateScope();
            var accessor = new CircuitServicesAccessor();
            var handler = new ServicesAccessorCircuitHandler(circuit.ServiceProvider, accessor);

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IServiceProvider?>? startedInside = null;
            await handler.CreateInboundActivityHandler(_ =>
            {
                startedInside = Task.Run(async () =>
                {
                    await release.Task;
                    return accessor.Services;
                });
                return Task.CompletedTask;
            })(null!);

            await handler.OnCircuitClosedAsync(null!, CancellationToken.None);
            release.SetResult();

            Assert.Null(await startedInside!);
        });
    }

    [Fact]
    public async Task Activity_failure_still_restores_the_caller_flow()
    {
        await Task.Run(async () =>
        {
            using var root = new ServiceCollection().BuildServiceProvider();
            using var circuit = root.CreateScope();
            var accessor = new CircuitServicesAccessor();
            var handler = new ServicesAccessorCircuitHandler(circuit.ServiceProvider, accessor);

            var activity = handler.CreateInboundActivityHandler(_ => throw new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => activity(null!));
            Assert.Null(accessor.Services);
        });
    }
}
