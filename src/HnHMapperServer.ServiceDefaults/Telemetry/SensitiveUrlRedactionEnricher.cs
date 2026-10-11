using Serilog.Core;
using Serilog.Events;

namespace HnHMapperServer.ServiceDefaults.Telemetry;

/// <summary>
/// Root Serilog enricher that masks secret-bearing URLs (<see cref="SensitiveUrlRedactor"/>) in every property of
/// every log event. Register it LAST in the <c>UseSerilog</c> lambda. MEL scope properties (ASP.NET Core's
/// <c>RequestPath</c>, IHttpClientFactory's <c>"HTTP {HttpMethod} {Uri}"</c> scope = Serilog's <c>Scope</c>
/// sequence) are attached by SerilogLoggerProvider before root enrichers run, so this covers the rendered message
/// and every sink: Console (docker logs), File and OTLP (via <c>writeToProviders</c>).
/// <para>String and Uri scalars and sequence elements are scanned; message-template text, exception messages and
/// structure/dictionary values are NOT - never interpolate URLs into a log template.</para>
/// </summary>
public sealed class SensitiveUrlRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? changed = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (Redact(value) is { } redacted)
                (changed ??= []).Add(new LogEventProperty(name, redacted));
        }

        if (changed is null)
            return;

        // Not while enumerating Properties
        foreach (var property in changed)
            logEvent.AddOrUpdateProperty(property);
    }

    private static LogEventPropertyValue? Redact(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string s } => SensitiveUrlRedactor.TryRedact(s, out var r) ? new ScalarValue(r) : null,
        ScalarValue { Value: Uri u } => SensitiveUrlRedactor.TryRedact(u.OriginalString, out var r) ? new ScalarValue(r) : null,
        SequenceValue sequence => RedactSequence(sequence),
        _ => null,
    };

    private static SequenceValue? RedactSequence(SequenceValue sequence)
    {
        LogEventPropertyValue[]? elements = null;
        for (var i = 0; i < sequence.Elements.Count; i++)
        {
            if (Redact(sequence.Elements[i]) is not { } redacted)
                continue;
            elements ??= [.. sequence.Elements];
            elements[i] = redacted;
        }

        return elements is null ? null : new SequenceValue(elements);
    }
}
