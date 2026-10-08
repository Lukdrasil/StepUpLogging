using System;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins the gated redaction: the per-thread slot the gate fills on reject and <see cref="StepUpSink"/> takes,
/// the redaction enricher registered behind the gate predicate, and the enricher's own sweep.
/// </summary>
public class RedactionShortcutGateTests
{
    private const string TokenPattern = "token=[^&]+";
    private const string RawToken = "token=abc";
    private const string Redacted = "[REDACTED]";

    [Fact]
    public void TakeSkipped_AfterReject_TrueOnce()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);
        var debug = Event(LogEventLevel.Debug);
        Assert.False(gate.Needs(debug));

        Assert.True(EnrichmentGate.TakeSkipped(debug));
        Assert.False(EnrichmentGate.TakeSkipped(debug));
    }

    [Fact]
    public void TakeSkipped_AfterAccept_IsFalse()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);
        var information = Event(LogEventLevel.Information);
        Assert.True(gate.Needs(information));

        Assert.False(EnrichmentGate.TakeSkipped(information));
    }

    [Fact]
    public void TakeSkipped_EarlierEventOnceLaterRejected_IsFalse()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);
        var earlier = Event(LogEventLevel.Debug);
        var later = Event(LogEventLevel.Debug);
        Assert.False(gate.Needs(earlier));
        Assert.False(gate.Needs(later));

        Assert.False(EnrichmentGate.TakeSkipped(earlier));
        Assert.True(EnrichmentGate.TakeSkipped(later));
    }

    [Fact]
    public void StepUpSink_GateSkippedDebugAfterSwitchLowered_NotExported()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        var gate = new EnrichmentGate(levelSwitch, LogEventLevel.Information);
        var collector = new CollectingSink();
        using var sink = new StepUpSink(Inner(collector), levelSwitch, LogEventLevel.Warning, []);
        var debug = Event(LogEventLevel.Debug);
        Assert.False(gate.Needs(debug));

        levelSwitch.MinimumLevel = LogEventLevel.Debug;
        sink.Emit(debug);

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_GateAcceptedInformationAfterSwitchLowered_Exported()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        var gate = new EnrichmentGate(levelSwitch, LogEventLevel.Information);
        var collector = new CollectingSink();
        using var sink = new StepUpSink(Inner(collector), levelSwitch, LogEventLevel.Warning, []);
        var information = Event(LogEventLevel.Information);
        Assert.True(gate.Needs(information));

        levelSwitch.MinimumLevel = LogEventLevel.Information;
        sink.Emit(information);

        Assert.Single(collector.Events);
    }

    [Fact]
    public void ApplyRedactionEnricher_SubFloorDebug_KeepsRawValue()
    {
        var token = TokenAfterRedaction(RedactionOn(), SwitchAtWarning(), logger => logger.Debug("{Token}", RawToken));

        Assert.Equal(RawToken, token);
    }

    [Fact]
    public void ApplyRedactionEnricher_InformationAtFloor_Redacted()
    {
        var token = TokenAfterRedaction(RedactionOn(), SwitchAtWarning(), logger => logger.Information("{Token}", RawToken));

        Assert.Equal(Redacted, token);
    }

    [Fact]
    public void ApplyRedactionEnricher_ImmediateDebug_Redacted()
    {
        var token = TokenAfterRedaction(
            RedactionOn(),
            SwitchAtWarning(),
            logger => logger.ForContext(LogProperties.IsImmediate, true).Debug("{Token}", RawToken));

        Assert.Equal(Redacted, token);
    }

    [Fact]
    public void ApplyRedactionEnricher_ErrorWithSwitchAtFatal_Redacted()
    {
        var needs = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Fatal), LogEventLevel.Fatal).Needs;

        var token = TokenAfterRedaction(RedactionOn(), needs, logger => logger.Error("{Token}", RawToken));

        Assert.Equal(Redacted, token);
    }

    [Fact]
    public void ApplyRedactionEnricher_FlagOff_InformationUnchanged()
    {
        var token = TokenAfterRedaction(
            new StepUpLoggingOptions { RedactLogEventProperties = false },
            SwitchAtWarning(),
            logger => logger.Information("{Token}", RawToken));

        Assert.Equal(RawToken, token);
    }

    [Fact]
    public void RedactionEnricher_TwoMatchingProperties_BothRedacted()
    {
        var enricher = new RedactionEnricher(Patterns());
        var logEvent = Event(
            LogEventLevel.Information,
            new LogEventProperty("First", new ScalarValue("token=one")),
            new LogEventProperty("Second", new ScalarValue("token=two")));

        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal(Redacted, ((ScalarValue)logEvent.Properties["First"]).Value);
        Assert.Equal(Redacted, ((ScalarValue)logEvent.Properties["Second"]).Value);
    }

    /// <summary>Runs <paramref name="scenario"/> through a Verbose root with only the gated redaction enricher and returns the logged <c>Token</c> value.</summary>
    private static object? TokenAfterRedaction(StepUpLoggingOptions opts, Func<LogEvent, bool> needsEnrichment, Action<Serilog.ILogger> scenario)
    {
        var collector = new CollectingSink();
        var lc = new LoggerConfiguration().MinimumLevel.Verbose();
        StepUpLoggingExtensions.ApplyRedactionEnricher(lc, opts, Patterns(), needsEnrichment);
        using (var logger = lc.WriteTo.Sink(collector).CreateLogger())
        {
            scenario(logger);
        }

        return ((ScalarValue)Assert.Single(collector.Events).Properties["Token"]).Value;
    }

    /// <summary>The gate predicate with the switch at Warning and <c>StepUpLevel</c> Information.</summary>
    private static Func<LogEvent, bool> SwitchAtWarning()
        => new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information).Needs;

    /// <summary>Options with property redaction switched on.</summary>
    private static StepUpLoggingOptions RedactionOn() => new() { RedactLogEventProperties = true };

    /// <summary>The single token pattern, compiled the way the host compiles it.</summary>
    private static CompiledRedactionPatterns Patterns() => new([StepUpLoggingExtensions.CompilePattern(TokenPattern)]);

    /// <summary>An output logger writing every level to <paramref name="collector"/>.</summary>
    private static Logger Inner(CollectingSink collector)
        => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();

    /// <summary>A bare event at <paramref name="level"/> carrying <paramref name="properties"/>.</summary>
    private static LogEvent Event(LogEventLevel level, params LogEventProperty[] properties)
        => new(DateTimeOffset.UtcNow, level, null, new MessageTemplateParser().Parse("shortcut"), properties);
}
