using System.Diagnostics;
using OpenTelemetry;

namespace HnHMapperServer.ServiceDefaults.Telemetry;

/// <summary>
/// Masks secret-bearing URLs (<see cref="SensitiveUrlRedactor"/>) in the URL tags of every span: ASP.NET Core
/// server spans (<c>/client/{token}</c>, <c>/invite/{code}</c>, ...) and System.Net.Http client spans (Web->API
/// invite/revoke calls, API->Discord webhooks). On .NET 10 those tags are set natively with only the query
/// redacted, and the instrumentation libraries deliberately leave them alone, so a processor is the one hook that
/// sees all of them. OnEnd runs after every instrumentation Enrich* callback and before the OTLP export processor,
/// which <c>UseOtlpExporter</c> always adds last.
/// </summary>
public sealed class SensitiveUrlRedactionProcessor : BaseProcessor<Activity>
{
    // Current semantic conventions plus the legacy names, in case an older instrumentation emits them.
    private static readonly string[] UrlTags = ["url.path", "url.full", "http.target", "http.url"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is string value && SensitiveUrlRedactor.TryRedact(value, out var redacted))
                activity.SetTag(tag, redacted);
        }
    }
}
