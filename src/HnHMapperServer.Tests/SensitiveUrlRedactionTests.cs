using System.Diagnostics;
using HnHMapperServer.ServiceDefaults.Telemetry;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;

namespace HnHMapperServer.Tests;

/// <summary>
/// The two adapters around SensitiveUrlRedactor: the Serilog root enricher (every log sink - Console, File, OTLP)
/// and the OpenTelemetry span processor (url.path / url.full of every span). All secrets here are fake.
/// </summary>
public class SensitiveUrlRedactionTests
{
    private const string S = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string G = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Serilog.Core.Logger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveUrlRedactionEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void Enricher_masks_template_properties_and_rendered_message()
    {
        var (logger, sink) = CreateLogger();

        logger.Warning("HTTP {RequestMethod} {RequestPath} responded {StatusCode}",
            "POST", $"/client/t1_{S}/positionUpdate", 401);

        var evt = Assert.Single(sink.Events);
        Assert.Equal("/client/t1_***/positionUpdate", ((ScalarValue)evt.Properties["RequestPath"]).Value);
        Assert.DoesNotContain(S, evt.RenderMessage());
        Assert.Contains("/client/t1_***/positionUpdate", evt.RenderMessage());
    }

    [Fact]
    public void Enricher_masks_Uri_typed_properties()
    {
        var (logger, sink) = CreateLogger();

        logger.Warning("API auth failure from {Uri}", new Uri($"http://api:8080/api/invitations/{G}/redeem"));

        var evt = Assert.Single(sink.Events);
        var rendered = evt.Properties["Uri"].ToString();
        Assert.Contains("/api/invitations/***/redeem", rendered);
        Assert.DoesNotContain(G, rendered);
        Assert.DoesNotContain(G, evt.RenderMessage());
    }

    [Fact]
    public void Enricher_masks_HttpClient_scope_sequence()
    {
        // IHttpClientFactory opens "HTTP {HttpMethod} {Uri}" around every call; Serilog stores the formatted scope as
        // a "Scope" SequenceValue element plus HttpMethod/Uri properties, on every line logged inside the handler.
        var (serilog, sink) = CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger("HnHMapperServer.Web.Services.AuthenticationDelegatingHandler");

        using (logger.BeginScope("HTTP {HttpMethod} {Uri}", "POST", $"http://api:8080/api/invitations/{G}/redeem"))
        {
            logger.LogWarning("API auth failure {Status}", 401);
        }

        var evt = Assert.Single(sink.Events);
        var scope = Assert.IsType<SequenceValue>(evt.Properties["Scope"]);
        var element = Assert.IsType<ScalarValue>(Assert.Single(scope.Elements));
        Assert.Equal("HTTP POST http://api:8080/api/invitations/***/redeem", element.Value);
        Assert.Equal("http://api:8080/api/invitations/***/redeem", ((ScalarValue)evt.Properties["Uri"]).Value);
        foreach (var (_, value) in evt.Properties)
            Assert.DoesNotContain(G, value.ToString());
    }

    [Fact]
    public void Enricher_masks_hosting_scope_RequestPath()
    {
        // ASP.NET Core's per-request scope (RequestId, RequestPath) becomes a property on every line logged during a
        // /client request, so each of those lines carries the token unless the enricher masks it.
        var (serilog, sink) = CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger("HnHMapperServer.Services.Services.GridService");

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r1", ["RequestPath"] = $"/client/t1_{S}/gridUpdate" }))
        {
            logger.LogInformation("Client in map {MapId}", 1);
        }

        var evt = Assert.Single(sink.Events);
        Assert.Equal("/client/t1_***/gridUpdate", ((ScalarValue)evt.Properties["RequestPath"]).Value);
    }

    [Fact]
    public void Enricher_does_not_reallocate_unrelated_properties()
    {
        var path = new ScalarValue("/map/api/v1/markers");
        var count = new ScalarValue(3);
        var sequence = new SequenceValue([new ScalarValue("HTTP GET http://api:8080/map/api/v1/markers")]);
        var evt = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null,
            new MessageTemplateParser().Parse("{Path} {Count} {Scope}"),
            [new LogEventProperty("Path", path), new LogEventProperty("Count", count), new LogEventProperty("Scope", sequence)]);

        new SensitiveUrlRedactionEnricher().Enrich(evt, null!);

        Assert.Same(path, evt.Properties["Path"]);
        Assert.Same(count, evt.Properties["Count"]);
        Assert.Same(sequence, evt.Properties["Scope"]);
    }

    [Fact]
    public void Processor_masks_url_tags_only()
    {
        var activity = new Activity("t"); // never started: no ActivityListener ever sees it
        activity.SetTag("url.path", $"/client/t1_{S}/positionUpdate");
        activity.SetTag("url.full", "https://discord.com/api/webhooks/1/AbC-xyz_9");
        activity.SetTag("http.route", "/client/{token}/positionUpdate");
        activity.SetTag("server.address", "api");

        new SensitiveUrlRedactionProcessor().OnEnd(activity);

        Assert.Equal("/client/t1_***/positionUpdate", activity.GetTagItem("url.path"));
        Assert.Equal("https://discord.com/api/webhooks/1/***", activity.GetTagItem("url.full"));
        Assert.Equal("/client/{token}/positionUpdate", activity.GetTagItem("http.route"));
        Assert.Equal("api", activity.GetTagItem("server.address"));
    }
}
