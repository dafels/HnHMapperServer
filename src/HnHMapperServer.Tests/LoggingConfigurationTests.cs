using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace HnHMapperServer.Tests;

/// <summary>
/// Serilog:MinimumLevel is the only level gate for Console, File and OTLP (UseSerilog(writeToProviders) +
/// ClearProviders make Logging:LogLevel inert). An environment file can overwrite keys but never remove them, so
/// a Debug override in a base appsettings.json reaches Production too: SignalR Debug logs circuit secrets and
/// every hub invocation. These tests merge the copied appsettings the way the host does (base, then environment),
/// and read the copied Program.cs files for the one wiring a test cannot reach otherwise (the UseSerilog lambda).
/// </summary>
public class LoggingConfigurationTests
{
    private static IConfigurationRoot Load(string service, string? environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(AppContext.BaseDirectory, "config", service))
            .AddJsonFile("appsettings.json", optional: false);
        if (environment != null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);
        return builder.Build();
    }

    private static void AssertNoLevelBelowInformation(string service, string? environment)
    {
        var levels = Load(service, environment).GetSection("Serilog:MinimumLevel")
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value != null)
            .ToList();

        Assert.Contains(levels, kv => kv.Key == "Default");
        foreach (var (key, value) in levels)
        {
            Assert.True(Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level),
                $"{service} ({environment ?? "base"}): Serilog:MinimumLevel:{key} = '{value}' is not a Serilog level");
            Assert.True(level >= LogEventLevel.Information,
                $"{service} ({environment ?? "base"}): Serilog:MinimumLevel:{key} = {value} - Debug belongs in appsettings.Development.json or a Serilog__MinimumLevel__Override__<Category> env var");
        }
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Web")]
    public void ProductionSerilogLevels_NeverBelowInformation(string service) =>
        AssertNoLevelBelowInformation(service, "Production");

    [Theory]
    [InlineData("Api")]
    [InlineData("Web")]
    public void BaseSerilogLevels_NeverBelowInformation(string service) =>
        // Every environment (a staging box, a local Production run) loads the base file
        AssertNoLevelBelowInformation(service, null);

    [Theory]
    [InlineData("Api")]
    [InlineData("Web")]
    public void UseSerilog_EndsWithServiceDefaults(string service)
    {
        // ServiceDefaultsTelemetryTests run ApplyServiceDefaults (FromLogContext + SensitiveUrlRedactionEnricher)
        // through the real Serilog -> OpenTelemetry pipeline; this pins that each service still calls it LAST in its
        // UseSerilog lambda. Without it every sink carries raw client tokens and invite codes again. Line comments
        // are stripped first, so a commented-out call fails too.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", service, "Program.cs.txt"));
        var code = Regex.Replace(source, @"//[^\r\n]*", "");
        var call = Regex.Match(code, @"\.UseSerilog\((?<lambda>.*?),\s*writeToProviders:\s*true\)", RegexOptions.Singleline);

        Assert.True(call.Success, $"{service}/Program.cs: no UseSerilog(..., writeToProviders: true) call found");
        Assert.True(Regex.IsMatch(call.Groups["lambda"].Value, @"\.ApplyServiceDefaults\(\)\s*$"),
            $"{service}/Program.cs: .ApplyServiceDefaults() must be the last call in the UseSerilog lambda");
    }

    [Fact]
    public void WebDevelopment_KeepsMapDiagnostics_ButNotSignalRDebug()
    {
        var overrides = Load("Web", "Development").GetSection("Serilog:MinimumLevel:Override");

        Assert.Equal("Debug", overrides["HnHMapperServer.Web.Components.Pages.Map"]);
        Assert.Equal("Debug", overrides["HnHMapperServer.Web.Components.Map.MapView"]);
        // SignalR / Http.Connections Debug logs circuit secrets and every hub invocation's arguments (form input):
        // enable it ad hoc with an env var, never in a committed file. null = inherits Microsoft.AspNetCore: Warning.
        foreach (var category in new[] { "Microsoft.AspNetCore.SignalR", "Microsoft.AspNetCore.Http.Connections" })
        {
            var value = overrides[category];
            Assert.True(value is null || (Enum.TryParse<LogEventLevel>(value, true, out var level) && level >= LogEventLevel.Information),
                $"{category} = {value} in Web Development config");
        }
    }
}
