using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins issue #66: <c>PathPropertyRedactionEnricher</c> redacts the path-bearing properties of every event,
/// <c>RequestPath</c> on any event and <c>Path</c> only on <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>
/// events, whatever <c>RedactLogEventProperties</c> is set to.
/// </summary>
public class PathPropertyRedactionEnricherTests
{
    private const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string ActionLinksPattern = "(?i)/Public/ActionLinks/[^/?]+";
    private const string ActionLinksPath = "/api/v2/Public/ActionLinks/abc123";
    private const string RedactedActionLinksPath = "/api/v2[REDACTED]";

    private static CompiledRedactionPatterns Patterns(params string[] patterns) =>
        new(patterns
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            .ToArray());

    private static CompiledRedactionPatterns TimingOutPatterns() =>
        new(new[] { new Regex("(a+)+$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1)) });

    private static ILogEventEnricher Enricher(CompiledRedactionPatterns patterns)
    {
        var type = typeof(CompiledRedactionPatterns).Assembly.GetType("Lukdrasil.StepUpLogging.PathPropertyRedactionEnricher");
        Assert.True(type is not null, "Lukdrasil.StepUpLogging declares no PathPropertyRedactionEnricher");
        var enricher = Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [patterns], null);
        return Assert.IsAssignableFrom<ILogEventEnricher>(enricher);
    }

    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    private static LogEvent Event(params (string Name, object? Value)[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("event"),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    private static LogEvent Enrich(CompiledRedactionPatterns patterns, params (string Name, object? Value)[] properties)
    {
        var evt = Event(properties);
        Enricher(patterns).Enrich(evt, new PropertyFactory());
        return evt;
    }

    private static object? Value(LogEvent evt, string property) =>
        Assert.IsType<ScalarValue>(evt.Properties[property]).Value;

    [Fact]
    public void RequestPath_IsRedacted_OnAnySourceContext()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("SourceContext", "MyApp.Handler"), ("RequestPath", ActionLinksPath));

        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath"));
    }

    [Fact]
    public void RequestPath_IsRedacted_WithoutSourceContext()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("RequestPath", ActionLinksPath));

        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath"));
    }

    [Fact]
    public void RequestPath_PathStringValue_IsRedacted()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("RequestPath", new PathString(ActionLinksPath)));

        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath")?.ToString());
    }

    [Fact]
    public void HostingPath_IsRedacted()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("SourceContext", HostingDiagnostics), ("Path", ActionLinksPath));

        Assert.Equal(RedactedActionLinksPath, Value(evt, "Path"));
    }

    [Fact]
    public void HostingPath_PathStringValue_IsRedacted()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("SourceContext", HostingDiagnostics), ("Path", new PathString(ActionLinksPath)));

        Assert.Equal(RedactedActionLinksPath, Value(evt, "Path")?.ToString());
    }

    [Theory]
    [InlineData("MyApp.Handler")]
    [InlineData("Microsoft.AspNetCore.Hosting")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics.Other")]
    public void Path_IsKept_OnOtherSourceContext(string sourceContext)
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("SourceContext", sourceContext), ("Path", ActionLinksPath), ("RequestPath", ActionLinksPath));

        Assert.Equal(ActionLinksPath, Value(evt, "Path"));
        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath"));
    }

    [Fact]
    public void Path_IsKept_WithoutSourceContext()
    {
        var evt = Enrich(Patterns(ActionLinksPattern), ("Path", ActionLinksPath), ("RequestPath", ActionLinksPath));

        Assert.Equal(ActionLinksPath, Value(evt, "Path"));
        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath"));
    }

    [Fact]
    public void OtherProperties_AreKept_EvenWhenPatternMatches()
    {
        var evt = Enrich(Patterns(ActionLinksPattern),
            ("SourceContext", HostingDiagnostics), ("Path", ActionLinksPath), ("RequestPath", ActionLinksPath),
            ("QueryString", "?next=" + ActionLinksPath), ("Target", ActionLinksPath));

        Assert.Equal("?next=" + ActionLinksPath, Value(evt, "QueryString"));
        Assert.Equal(ActionLinksPath, Value(evt, "Target"));
        Assert.Equal(RedactedActionLinksPath, Value(evt, "Path"));
        Assert.Equal(RedactedActionLinksPath, Value(evt, "RequestPath"));
    }

    [Fact]
    public void RedactedValue_IsNotTrimmed()
    {
        var evt = Enrich(Patterns("abc123"), ("RequestPath", "  /x/abc123  "));

        Assert.Equal("  /x/[REDACTED]  ", Value(evt, "RequestPath"));
    }

    [Fact]
    public void UnmatchedValue_KeepsItsProperty()
    {
        var path = new PathString("/api/v2/Other/Thing/xyz789");
        var evt = Event(("SourceContext", HostingDiagnostics), ("Path", path), ("RequestPath", "/api/v2/Other/Thing/xyz789"));
        var pathBefore = evt.Properties["Path"];
        var requestPathBefore = evt.Properties["RequestPath"];

        Enricher(Patterns(ActionLinksPattern)).Enrich(evt, new PropertyFactory());

        Assert.Same(pathBefore, evt.Properties["Path"]);
        Assert.Same(requestPathBefore, evt.Properties["RequestPath"]);
    }

    [Fact]
    public void NonPathScalar_IsKept()
    {
        var evt = Enrich(Patterns("42"), ("RequestPath", 42));

        Assert.Equal(42, Value(evt, "RequestPath"));
    }

    [Fact]
    public void NoPattern_KeepsEveryProperty()
    {
        var evt = Event(("SourceContext", HostingDiagnostics), ("Path", ActionLinksPath), ("RequestPath", ActionLinksPath));
        var before = evt.Properties.ToDictionary(p => p.Key, p => p.Value);

        Enricher(Patterns()).Enrich(evt, new PropertyFactory());

        Assert.Equal(before.Count, evt.Properties.Count);
        foreach (var (key, value) in before)
        {
            Assert.Same(value, evt.Properties[key]);
        }
    }

    [Fact]
    public void RedactionTimeout_FailsClosed_OnRequestPathAndHostingPath()
    {
        var secret = "/api/" + new string('a', 40) + "!";

        var evt = Enrich(TimingOutPatterns(), ("SourceContext", HostingDiagnostics), ("Path", secret), ("RequestPath", secret));

        Assert.Equal(CompiledRedactionPatterns.RedactionError, Value(evt, "Path"));
        Assert.Equal(CompiledRedactionPatterns.RedactionError, Value(evt, "RequestPath"));
    }

    [Fact]
    public async Task RedactionTimeout_StillExportsTheEvent_WithFailClosedPath()
    {
        var secret = "/api/" + new string('a', 40) + "!";

        var events = await PathHost.SendAsync(secret, TimingOutPatterns());

        var handler = Assert.Single(events, e => e.MessageTemplate.Text == PathHost.HandlerInformation);
        Assert.Equal(CompiledRedactionPatterns.RedactionError, Value(handler, "RequestPath"));
        Assert.DoesNotContain(events, e => e.Properties.Values.Any(v => v.ToString().Contains(new string('a', 40), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NoPattern_ExportsScopeRequestPathRaw()
    {
        var events = await PathHost.SendAsync(ActionLinksPath, null);

        var handler = Assert.Single(events, e => e.MessageTemplate.Text == PathHost.HandlerInformation);
        Assert.Equal(ActionLinksPath, Value(handler, "RequestPath"));
        var hosting = events.Where(e => Scalar(e, "SourceContext") == HostingDiagnostics && e.Properties.ContainsKey("Path")).ToList();
        Assert.NotEmpty(hosting);
        Assert.All(hosting, e => Assert.Equal(ActionLinksPath, Scalar(e, "Path")));
    }

    [Fact]
    public async Task NoPattern_RegistersNoPathPropertyRedactionEnricher()
    {
        var events = await PathHost.SendAsync(ActionLinksPath, null, ("RedactionRegexes:0", " "));

        var handler = Assert.Single(events, e => e.MessageTemplate.Text == PathHost.HandlerInformation);
        Assert.Equal(ActionLinksPath, Value(handler, "RequestPath"));
    }

    private static string? Scalar(LogEvent evt, string property) =>
        evt.Properties.TryGetValue(property, out var value) ? (value as ScalarValue)?.Value?.ToString() : null;
}

/// <summary>
/// A real <c>AddStepUpLogging</c> TestServer host whose exported events are captured through a config-declared
/// <c>Serilog:WriteTo</c> sink, so the capture sees exactly what the gated, buffer-flush and bypass loggers export,
/// scope properties included.
/// </summary>
internal static class PathHost
{
    public const string HandlerInformation = "Handler information";
    public const string HandlerError = "Handler error";

    public static async Task<LogEvent[]> SendAsync(string path, CompiledRedactionPatterns? patterns, params (string Key, string Value)[] overrides)
    {
        var captureKey = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:Mode"] = "Auto",
            ["SerilogStepUp:BaseLevel"] = "Warning",
            ["SerilogStepUp:StepUpLevel"] = "Information",
            ["SerilogStepUp:DurationSeconds"] = "300",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
            ["SerilogStepUp:AlwaysLogRequestSummary"] = "true",
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
            ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
            ["Serilog:WriteTo:0:Args:key"] = captureKey,
        };
        foreach (var (key, value) in overrides)
        {
            settings["SerilogStepUp:" + key] = value;
        }

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.AddStepUpLogging();
            if (patterns is not null)
            {
                builder.Services.AddSingleton(patterns);
            }

            await using (var app = builder.Build())
            {
                app.UseStepUpRequestLogging();
                app.MapGet("/api/{**rest}", (ILogger<PathHostHandler> logger) =>
                {
                    logger.LogInformation(HandlerInformation);
                    logger.LogError(HandlerError);
                    return "ok";
                });

                await app.StartAsync(TestContext.Current.CancellationToken);
                using var client = app.GetTestClient();
                using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
                await Task.Delay(200, TestContext.Current.CancellationToken);
                await app.StopAsync(TestContext.Current.CancellationToken);
            }

            return KeyedCaptureSink.Events(captureKey);
        }
        finally
        {
            KeyedCaptureSink.Forget(captureKey);
        }
    }
}

internal sealed class PathHostHandler;
