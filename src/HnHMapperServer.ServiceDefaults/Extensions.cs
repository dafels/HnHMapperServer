using HnHMapperServer.ServiceDefaults.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // NOTE: Resilience is NOT added by default because file upload streams
            // cannot be retried. Add resilience explicitly on clients that need it.
            // http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Allow both HTTP and HTTPS for service discovery resolution
        // In production Docker Compose, internal traffic uses HTTP (api:8080)
        // In development with Aspire, HTTPS is used (https://api)
        builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        {
            options.AllowedSchemes = ["https", "http"];
        });

        return builder;
    }

    /// <summary>
    /// The Serilog setup both services share. Call it LAST in the <c>UseSerilog</c> lambda (after <c>ReadFrom.*</c>),
    /// so the redaction enricher runs after every other root enricher. ServiceDefaultsTelemetryTests run this method;
    /// LoggingConfigurationTests check that Api/Program.cs and Web/Program.cs still end their lambda with it.
    /// </summary>
    public static LoggerConfiguration ApplyServiceDefaults(this LoggerConfiguration configuration) => configuration
        .Enrich.FromLogContext()
        // Masks client tokens, invite codes, token-revoke paths and webhook tokens in every property (incl. the
        // hosting scope's RequestPath and HttpClient's Scope/Uri) for all sinks. See SensitiveUrlRedactor.
        .Enrich.With<SensitiveUrlRedactionEnricher>();

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // .NET 10 meters. Blazor's are no-ops until something subscribes, so
                    // naming them here is what turns them on.
                    //   aspnetcore.components.circuit.active     - circuits in memory
                    //   aspnetcore.components.circuit.connected  - circuits with a live client
                    //   aspnetcore.components.circuit.duration    - circuit lifetime
                    // active minus connected is the retained-circuit backlog, which is the
                    // number that predicts Blazor Server memory. (These carry no tags, so
                    // they cannot be split by tenant or route.)
                    .AddMeter("Microsoft.AspNetCore.Components.Server.Circuits")
                    .AddMeter("Microsoft.AspNetCore.Components")
                    .AddMeter("Microsoft.AspNetCore.Components.Lifecycle")
                    // Kestrel's pools evict idle blocks in .NET 10; these show how much is
                    // pooled vs rented vs handed back.
                    .AddMeter("Microsoft.AspNetCore.MemoryPool")
                    // New in .NET 10: sign-in / authorization counters.
                    .AddMeter("Microsoft.AspNetCore.Identity")
                    .AddMeter("Microsoft.AspNetCore.Authorization");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    // Masks client tokens, invite codes and webhook tokens in url.path/url.full of every span
                    // (server and HttpClient alike). Runs before the OTLP exporter, which UseOtlpExporter adds last.
                    .AddProcessor(new SensitiveUrlRedactionProcessor())
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Exclude health check requests from tracing
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);

                        // No span per SignalR hub message (on by default on .NET 9+). Our only hub is Blazor's
                        // ComponentHub, whose JS-interop / render-ack messages would otherwise make up most traces
                        // (each a sub-millisecond span). With the source off, ASP.NET Core leaves
                        // Activity.Current null during a hub call, so HTTP calls and timers started from it become
                        // their own root traces. Blazor's Route/Event and Circuit spans are unaffected. Filter
                        // can't do this (it only sees HTTP requests). Turn back on if a real hub is ever added.
                        options.EnableAspNetCoreSignalRSupport = false;
                    })
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation()
                    // .NET 10 activity sources: circuit starts, Blazor navigations and
                    // event handling. Circuits moved to their own source in preview 6.
                    .AddSource("Microsoft.AspNetCore.Components.Server.Circuits")
                    .AddSource("Microsoft.AspNetCore.Components");
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
