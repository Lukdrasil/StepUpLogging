using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class EnrichmentGateTests
{
    private const string AlwaysCategory = "Test.Always";

    [Fact]
    public void Needs_DebugWithSwitchAtWarning_IsFalse()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);

        Assert.False(gate.Needs(Event(LogEventLevel.Debug)));
    }

    [Fact]
    public void Needs_InformationWithSwitchAtWarning_IsTrue()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);

        Assert.True(gate.Needs(Event(LogEventLevel.Information)));
    }

    [Fact]
    public void Needs_SwitchSetToDebug_DebugIsTrue()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        var gate = new EnrichmentGate(levelSwitch, LogEventLevel.Information);
        Assert.False(gate.Needs(Event(LogEventLevel.Debug)));

        levelSwitch.MinimumLevel = LogEventLevel.Debug;

        Assert.True(gate.Needs(Event(LogEventLevel.Debug)));
    }

    [Fact]
    public void Needs_StepUpLevelFatal_ErrorIsTrue()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Fatal), LogEventLevel.Fatal);

        Assert.True(gate.Needs(Event(LogEventLevel.Error)));
        Assert.False(gate.Needs(Event(LogEventLevel.Warning)));
    }

    [Fact]
    public void Needs_DebugMarkedImmediate_IsTrue()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);

        Assert.True(gate.Needs(Event(LogEventLevel.Debug, new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true)))));
    }

    [Fact]
    public void Needs_DebugMarkedRequestSummary_IsTrue()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);

        Assert.True(gate.Needs(Event(LogEventLevel.Debug, new LogEventProperty(LogProperties.IsRequestSummary, new ScalarValue(true)))));
    }

    [Fact]
    public void Needs_ImmediateMarkerFalse_IsFalse()
    {
        var gate = new EnrichmentGate(new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information);

        Assert.False(gate.Needs(Event(LogEventLevel.Debug, new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(false)))));
    }

    [Fact]
    public void For_WithConfigureHook_NeedsDebug()
    {
        var needs = EnrichmentGate.For(
            new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information, (_, _) => { }, Configuration([]));

        Assert.True(needs(Event(LogEventLevel.Debug)));
    }

    [Fact]
    public void For_WithAuditToEntry_NeedsDebug()
    {
        var needs = EnrichmentGate.For(
            new LoggingLevelSwitch(LogEventLevel.Warning),
            LogEventLevel.Information,
            configure: null,
            Configuration(new() { ["Serilog:AuditTo:0:Name"] = "Console" }));

        Assert.True(needs(Event(LogEventLevel.Debug)));
    }

    [Fact]
    public void For_WithoutRootSinks_SkipsDebug()
    {
        var needs = EnrichmentGate.For(
            new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Information, configure: null, Configuration([]));

        Assert.False(needs(Event(LogEventLevel.Debug)));
        Assert.True(needs(Event(LogEventLevel.Warning)));
    }

    [Fact]
    public void ApplyRootEnrichers_GateYes_EventCarriesApplication()
    {
        var collector = Root(new StepUpLoggingOptions(), _ => true, logger => logger.Debug("gate yes"));

        Assert.Contains("Application", Assert.Single(collector.Events).Properties.Keys);
    }

    [Fact]
    public void ApplyRootEnrichers_GateNo_DebugCarriesNoApplication()
    {
        var collector = Root(new StepUpLoggingOptions(), _ => false, logger => logger.Debug("gate no"));

        Assert.DoesNotContain("Application", Assert.Single(collector.Events).Properties.Keys);
    }

    [Fact]
    public void ApplyRootEnrichers_AlwaysExportDebug_IsImmediateAndEnriched()
    {
        var collector = Root(
            new StepUpLoggingOptions { AlwaysExportCategories = [AlwaysCategory] },
            logEvent => LogProperties.HasFlag(logEvent, LogProperties.IsImmediate),
            logger => logger.ForContext("SourceContext", AlwaysCategory).Debug("always export"));

        var evt = Assert.Single(collector.Events);
        Assert.True(LogProperties.HasFlag(evt, LogProperties.IsImmediate));
        Assert.Contains("Application", evt.Properties.Keys);
    }

    private static CollectingSink Root(StepUpLoggingOptions opts, Func<LogEvent, bool> needsEnrichment, Action<Serilog.ILogger> scenario)
    {
        var collector = new CollectingSink();
        var lc = new LoggerConfiguration().MinimumLevel.Verbose();
        StepUpLoggingExtensions.ApplyRootEnrichers(lc, TestHosts.NewBuilder(), opts, new CompiledRedactionPatterns([]), needsEnrichment);
        using var logger = lc.WriteTo.Sink(collector).CreateLogger();
        scenario(logger);
        return collector;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static LogEvent Event(LogEventLevel level, params LogEventProperty[] properties)
        => new(DateTimeOffset.UtcNow, level, null, new MessageTemplateParser().Parse("gate"), properties);
}
