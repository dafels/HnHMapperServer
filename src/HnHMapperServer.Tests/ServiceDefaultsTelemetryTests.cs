using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Serilog;

namespace HnHMapperServer.Tests;

/// <summary>
/// ActivityListeners are process-global: a TracerProvider built in one test sees activities started by another,
/// so every test that builds one runs in this non-parallel collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenTelemetryListenerCollection
{
    public const string Name = "OpenTelemetry listeners";
}

/// <summary>
/// Pins what ServiceDefaults' ConfigureOpenTelemetry subscribes and exports: no span per SignalR hub message (Blazor's
/// ComponentHub messages would otherwise dominate the traces), and no client token / invite code / webhook token
/// in anything that leaves the process - logs through the real Serilog -> OpenTelemetry provider pipeline with
/// IncludeScopes, spans through the redaction processor ahead of the exporter. All secrets here are fake.
/// </summary>
[Collection(OpenTelemetryListenerCollection.Name)]
public class ServiceDefaultsTelemetryTests
{
    private const string S = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string G = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string W = "WebhookTokenAbC-xyz_9";

    [Fact]
    public async Task Tracing_DropsSignalRHubSpans_KeepsBlazorAndHttpSpans()
    {
        var builder = WebApplication.CreateBuilder();
        builder.ConfigureOpenTelemetry();
        await using var app = builder.Build();
        _ = app.Services.GetRequiredService<TracerProvider>(); // builds the provider = registers its ActivityListener

        Assert.False(IsRecorded("Microsoft.AspNetCore.SignalR.Server", "Microsoft.AspNetCore.SignalR.Server.InvocationIn"));
        Assert.True(IsRecorded("Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.HandleEvent"));
        Assert.True(IsRecorded("Microsoft.AspNetCore.Components.Server.Circuits", "Microsoft.AspNetCore.Components.Server.Circuits.StartCircuit"));
        Assert.True(IsRecorded("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.HttpRequestIn"));
        Assert.True(IsRecorded("System.Net.Http", "System.Net.Http.HttpRequestOut"));
    }

    private static bool IsRecorded(string sourceName, string operation)
    {
        using var source = new ActivitySource(sourceName);
        using var activity = source.CreateActivity(operation, ActivityKind.Internal);
        return activity is { Recorded: true };
    }

    [Fact]
    public void Redaction_runs_before_exporter()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureOpenTelemetry();
        var exporter = new CollectingActivityExporter();
        // Registered after ConfigureOpenTelemetry, like UseOtlpExporter's processor (documented to be added last)
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource(builder.Environment.ApplicationName);
        using (var activity = source.StartActivity("POST", ActivityKind.Client))
        {
            Assert.NotNull(activity);
            activity.SetTag("url.full", $"http://api:8080/api/invitations/{G}/redeem");
        }

        var exported = Assert.Single(exporter.Exported, e => e.Source == builder.Environment.ApplicationName);
        Assert.Equal("http://api:8080/api/invitations/***/redeem", exported.UrlFull);
    }

    [Fact]
    public void Exported_log_records_carry_no_secret()
    {
        var collector = new CollectingLogProcessor();
        var builder = Host.CreateApplicationBuilder();
        // Same order as Api/Program.cs and Web/Program.cs: UseSerilog(writeToProviders) -> ClearProviders -> AddServiceDefaults
        builder.Services.AddSerilog((_, c) => c
                .MinimumLevel.Debug()
                .ApplyServiceDefaults(), // the services' own enrichers (LoggingConfigurationTests pin the call sites)
            preserveStaticLogger: true, writeToProviders: true);
        builder.Logging.ClearProviders();
        builder.ConfigureOpenTelemetry(); // IncludeScopes + IncludeFormattedMessage
        builder.Logging.AddOpenTelemetry(o => o.AddProcessor(collector));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HnHMapperServer.Services.Services.GridService");

        // ASP.NET Core's hosting scope and IHttpClientFactory's scope, as they wrap the API's Discord dispatch
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r1", ["RequestPath"] = $"/client/t1_{S}/gridUpdate" }))
        using (logger.BeginScope("HTTP {HttpMethod} {Uri}", "POST", $"https://discord.com/api/webhooks/1/{W}"))
        {
            logger.LogInformation("Client in map {MapId}", 1);
        }

        Assert.Contains("RequestPath=/client/t1_***/gridUpdate", collector.Values);
        Assert.Contains(collector.Values, v => v.Contains("https://discord.com/api/webhooks/1/***"));
        Assert.Contains(collector.Values, v => v == "Client in map 1"); // the formatted message itself
        Assert.DoesNotContain(collector.Values, v => v.Contains(S) || v.Contains(W));
        // Scopes reach OpenTelemetry only as (redacted) Serilog properties - there is no raw MEL scope channel
        Assert.DoesNotContain(collector.ScopeValues, v => v.Contains(S) || v.Contains(W));
    }

    [Fact]
    public async Task Otlp_export_carries_no_secret()
    {
        // A local OTLP/HTTP receiver: the real UseOtlpExporter path, read back as raw protobuf bytes
        // (strings are stored verbatim in protobuf, so a substring search over the body is exact enough).
        var bodies = new ConcurrentQueue<(string Path, byte[] Body)>();
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        receiverBuilder.Logging.ClearProviders();
        await using var receiver = receiverBuilder.Build();
        receiver.MapPost("/v1/{signal}", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            bodies.Enqueue((context.Request.Path.Value ?? "", body.ToArray()));
            return Results.Ok();
        });
        await receiver.StartAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.Urls.Single();
        builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        builder.Services.AddSerilog((_, c) => c
                .MinimumLevel.Debug()
                .ApplyServiceDefaults(), // the services' own enrichers (LoggingConfigurationTests pin the call sites)
            preserveStaticLogger: true, writeToProviders: true);
        builder.Logging.ClearProviders();
        builder.ConfigureOpenTelemetry();
        using (var host = builder.Build())
        {
            await host.StartAsync(); // like the services: the hosted service builds every provider
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            var loggerProvider = host.Services.GetRequiredService<LoggerProvider>();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HnHMapperServer.Api.Endpoints.ClientEndpoints");
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = $"/client/t1_{S}/gridUpdate" }))
            {
                logger.LogWarning("PositionUpdate: Skipping character ID={CharacterId}", 7);
            }

            using var source = new ActivitySource(builder.Environment.ApplicationName);
            using (var activity = source.StartActivity("POST /client/{token}/positionUpdate", ActivityKind.Server))
            {
                Assert.NotNull(activity);
                activity.SetTag("url.path", $"/client/t1_{S}/positionUpdate");
            }
            using (var activity = source.StartActivity("POST", ActivityKind.Client))
            {
                activity!.SetTag("url.full", $"https://discord.com/api/webhooks/1/{W}");
            }

            Assert.True(tracerProvider.ForceFlush(10_000));
            Assert.True(loggerProvider.ForceFlush(10_000));
            await host.StopAsync();
        }

        string Text(string path) => string.Concat(bodies.Where(b => b.Path == path).Select(b => Encoding.UTF8.GetString(b.Body)));
        var traces = Text("/v1/traces");
        var logs = Text("/v1/logs");
        Assert.Contains("/client/t1_***/positionUpdate", traces);
        Assert.Contains("https://discord.com/api/webhooks/1/***", traces);
        Assert.Contains("/client/t1_***/gridUpdate", logs);
        foreach (var (_, body) in bodies)
        {
            var text = Encoding.UTF8.GetString(body);
            Assert.DoesNotContain(S, text);
            Assert.DoesNotContain(W, text);
        }
    }

    private sealed class CollectingActivityExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<(string Source, string? UrlFull)> Exported { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Exported.Enqueue((activity.Source.Name, activity.GetTagItem("url.full") as string));
            return ExportResult.Success;
        }
    }

    /// <summary>LogRecord is pooled: copy everything inside OnEnd.</summary>
    private sealed class CollectingLogProcessor : BaseProcessor<LogRecord>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _values = [];
        private readonly List<string> _scopeValues = [];

        public IReadOnlyList<string> Values { get { lock (_gate) return [.. _values]; } }
        public IReadOnlyList<string> ScopeValues { get { lock (_gate) return [.. _scopeValues]; } }

        public override void OnEnd(LogRecord record)
        {
            lock (_gate)
            {
                if (record.FormattedMessage is { } message)
                    _values.Add(message);
                foreach (var (key, value) in record.Attributes ?? [])
                    _values.Add($"{key}={value}");
                record.ForEachScope(static (scope, list) =>
                {
                    foreach (var (key, value) in scope)
                        list.Add($"{key}={value}");
                }, _scopeValues);
            }
        }
    }
}
