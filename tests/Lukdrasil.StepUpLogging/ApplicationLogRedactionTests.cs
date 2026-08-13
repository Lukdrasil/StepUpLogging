using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class ApplicationLogRedactionTests
{
    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static LogEvent MakeEvent(params LogEventProperty[] properties)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("msg"), properties);
    }

    // ─── direct enricher tests ─────────────────────────────────────────────────

    [Fact]
    public void Enrich_RedactsMatchingScalarStringProperty()
    {
        var patterns = new CompiledRedactionPatterns([Compile("token=[^&]+")]);
        var enricher = new RedactionEnricher(patterns);
        var logEvent = MakeEvent(new LogEventProperty("Message", new ScalarValue("token=secret123")));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal("[REDACTED]", ((ScalarValue)logEvent.Properties["Message"]).Value);
    }

    [Fact]
    public void Enrich_NonMatchingStringProperty_LeftUnchanged()
    {
        var patterns = new CompiledRedactionPatterns([Compile("token=[^&]+")]);
        var enricher = new RedactionEnricher(patterns);
        var logEvent = MakeEvent(new LogEventProperty("Message", new ScalarValue("nothing to see here")));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal("nothing to see here", ((ScalarValue)logEvent.Properties["Message"]).Value);
    }

    [Fact]
    public void Enrich_NonStringScalarProperty_LeftUntouched()
    {
        var patterns = new CompiledRedactionPatterns([Compile(".*")]);
        var enricher = new RedactionEnricher(patterns);
        var logEvent = MakeEvent(new LogEventProperty("Count", new ScalarValue(42)));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal(42, ((ScalarValue)logEvent.Properties["Count"]).Value);
    }

    [Fact]
    public void Enrich_StructureValue_NotRecursedInto()
    {
        var patterns = new CompiledRedactionPatterns([Compile(".*")]);
        var enricher = new RedactionEnricher(patterns);
        var structure = new StructureValue(new[] { new LogEventProperty("Secret", new ScalarValue("hunter2")) });
        var logEvent = MakeEvent(new LogEventProperty("User", structure));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Same(structure, logEvent.Properties["User"]);
    }

    [Fact]
    public void Enrich_SequenceValue_LeftUntouched()
    {
        var patterns = new CompiledRedactionPatterns([Compile(".*")]);
        var enricher = new RedactionEnricher(patterns);
        var sequence = new SequenceValue(new LogEventPropertyValue[] { new ScalarValue("hunter2") });
        var logEvent = MakeEvent(new LogEventProperty("Tags", sequence));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Same(sequence, logEvent.Properties["Tags"]);
    }

    [Fact]
    public void Enrich_DictionaryValue_LeftUntouched()
    {
        var patterns = new CompiledRedactionPatterns([Compile(".*")]);
        var enricher = new RedactionEnricher(patterns);
        var dictionary = new DictionaryValue(new[]
        {
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("k"), new ScalarValue("hunter2")),
        });
        var logEvent = MakeEvent(new LogEventProperty("Map", dictionary));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Same(dictionary, logEvent.Properties["Map"]);
    }

    [Theory]
    [InlineData("ServiceInstanceId")]
    [InlineData("TraceId")]
    [InlineData("SpanId")]
    public void Enrich_ExcludedProperty_NeverRedacted_EvenWhenPatternMatches(string excludedName)
    {
        // A GUID-in-"N"-form-shaped pattern is exactly the API-key shape D6 argues from.
        var patterns = new CompiledRedactionPatterns([Compile("[0-9a-f]{32}")]);
        var enricher = new RedactionEnricher(patterns);
        var guid = Guid.NewGuid().ToString("N");
        var logEvent = MakeEvent(new LogEventProperty(excludedName, new ScalarValue(guid)));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal(guid, ((ScalarValue)logEvent.Properties[excludedName]).Value);
    }

    [Fact]
    public void Enrich_UnchangedValue_DoesNotCreateReplacementProperty()
    {
        var patterns = new CompiledRedactionPatterns(Array.Empty<Regex>());
        var enricher = new RedactionEnricher(patterns);
        var original = new ScalarValue("nothing to redact");
        var logEvent = MakeEvent(new LogEventProperty("Message", original));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Same(original, logEvent.Properties["Message"]);
    }

    // ─── end-to-end pipeline tests ─────────────────────────────────────────────

    [Fact]
    public void FlagOn_PatternConfigured_ApplicationLogProperty_IsRedactedAtSink()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
            ["SerilogStepUp:Mode"] = "AlwaysOn",
            ["SerilogStepUp:RedactLogEventProperties"] = "true",
            ["SerilogStepUp:RedactionRegexes:0"] = "token=[^&]+",
        });
        var collector = new CollectingSink();
        builder.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(collector));

        using (var host = builder.Build())
        {
            var logger = host.Services.GetRequiredService<Serilog.ILogger>();
            logger.Information("checkout with {Token}", "token=super-secret");
        }

        var evt = Assert.Single(collector.Events);
        Assert.Equal("[REDACTED]", ((ScalarValue)evt.Properties["Token"]).Value);
    }

    [Fact]
    public void FlagOff_ApplicationLogProperty_IsUnchangedFromToday()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
            ["SerilogStepUp:Mode"] = "AlwaysOn",
            ["SerilogStepUp:RedactLogEventProperties"] = "false",
            ["SerilogStepUp:RedactionRegexes:0"] = "token=[^&]+",
        });
        var collector = new CollectingSink();
        builder.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(collector));

        using (var host = builder.Build())
        {
            var logger = host.Services.GetRequiredService<Serilog.ILogger>();
            logger.Information("checkout with {Token}", "token=super-secret");
        }

        var evt = Assert.Single(collector.Events);
        Assert.Equal("token=super-secret", ((ScalarValue)evt.Properties["Token"]).Value);
    }

    [Fact]
    public void FlagOn_PropertyStampedByConfigureHookEnricher_IsStillRedacted()
    {
        // Pins ADR 0022 D5: the redaction enricher must run AFTER configure?.Invoke, not before —
        // otherwise a property a consumer's own enricher adds through the configure hook escapes
        // the sweep entirely.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
            ["SerilogStepUp:Mode"] = "AlwaysOn",
            ["SerilogStepUp:RedactLogEventProperties"] = "true",
            ["SerilogStepUp:RedactionRegexes:0"] = "consumer-secret",
        });
        var collector = new CollectingSink();
        builder.AddStepUpLogging((_, lc) => lc
            .Enrich.WithProperty("ConsumerStamped", "consumer-secret")
            .WriteTo.Sink(collector));

        using (var host = builder.Build())
        {
            var logger = host.Services.GetRequiredService<Serilog.ILogger>();
            logger.Information("event");
        }

        var evt = Assert.Single(collector.Events);
        Assert.Equal("[REDACTED]", ((ScalarValue)evt.Properties["ConsumerStamped"]).Value);
    }
}
