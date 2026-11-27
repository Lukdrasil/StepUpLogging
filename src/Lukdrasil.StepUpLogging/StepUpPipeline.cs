using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Configuration;
using Serilog.Enrichers.CallStack;
using Serilog.Enrichers.OpenTelemetry;
using Serilog.Events;
using Serilog.Exceptions;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Builds the Serilog pipeline: the root logger at Verbose with its gated enrichers, the gated output logger and the
/// bypass logger, and the root sinks in their fixed order. The order of enrichers and sinks is the contract of
/// ADR 0003, ADR 0005, ADR 0012, ADR 0022 and ADR 0026, and this is the only place that sets it.
/// </summary>
/// <remarks>
/// The bypass logger is built here, not resolved through DI: <c>AddSerilog</c> registers <see cref="ILogger"/> as a
/// factory that depends on <c>ILoggerFactory</c>, which depends on this very callback, so resolving it here deadlocks.
/// The constructor takes what the enrichers need; <see cref="Configure"/> takes what only the full build needs, the
/// controller and the services of the <c>AddSerilog</c> callback.
/// </remarks>
internal sealed class StepUpPipeline(IHostApplicationBuilder builder, StepUpSettings settings, CompiledRedactionPatterns patterns, string? logFilePath)
{
    private StepUpLoggingOptions Options => settings.Options;

    /// <summary>
    /// Configures <paramref name="lc"/> as the root logger, attaches the bypass logger to <paramref name="controller"/>
    /// and writes the startup report through it. <paramref name="configure"/> runs after the library's sinks and
    /// before the redaction enricher, so a property the consumer adds is redacted too (ADR 0022 D5).
    /// </summary>
    internal void Configure(LoggerConfiguration lc, StepUpLoggingController controller, IServiceProvider services, Action<IServiceProvider, LoggerConfiguration>? configure, IConfigurationSection optionsSection)
    {
        var (rootConfig, gatedConfig) = SplitSerilogConfiguration(builder.Configuration);
        SerilogConfigurationAssemblies.ReadSerilogSection(lc, rootConfig).MinimumLevel.Verbose();

        var needsEnrichment = EnrichmentGate.For(controller.LevelSwitch, settings.StepUpLevel, configure, builder.Configuration);
        ApplyRootEnrichers(lc, needsEnrichment);

        var bypassLogger = CreateBypassLogger(gatedConfig);
        controller.SetSummaryLogger(bypassLogger);
        StartupReport.Write(bypassLogger, settings, optionsSection, logFilePath, controller.DiagnosticExpiresAt);

        WriteRootSinks(lc, controller, CreateGatedLogger(gatedConfig), bypassLogger);
        configure?.Invoke(services, lc);
        ApplyRedactionEnricher(lc, needsEnrichment);
    }

    /// <summary>
    /// Writes the root sinks in pipeline order: the step-up gate to <paramref name="gatedOutput"/>, the pre-error
    /// buffer, the trigger, and the two bypass routes to <paramref name="bypassLogger"/>.
    /// </summary>
    private void WriteRootSinks(LoggerConfiguration lc, StepUpLoggingController controller, ILogger gatedOutput, ILogger bypassLogger)
    {
        var preErrorBuffer = Options.EnablePreErrorBuffering
            ? new PreErrorBufferSink(bypassLogger, Options.PreErrorBufferSize, Options.PreErrorMaxContexts, settings.StepUpLevel, settings.PreErrorBufferExcludeCategories)
            : null;

        lc.WriteTo.Sink(new StepUpSink(
            gatedOutput,
            controller.LevelSwitch,
            settings.BaseLevel,
            settings.NeverStepUpCategories,
            preErrorBuffer,
            settings.CategoryFloors,
            () => controller.IsDiagnosticActive));

        if (preErrorBuffer is not null)
        {
            lc.WriteTo.Sink(preErrorBuffer);
        }

        lc.WriteTo.Sink(new StepUpTriggerSink(controller, settings.NeverTriggerCategories));
        lc.WriteTo.Sink(BypassSink.RequestSummaries(bypassLogger));
        lc.WriteTo.Sink(BypassSink.Immediate(bypassLogger));
    }

    /// <summary>
    /// Partitions the application configuration into two Serilog views so that config-declared
    /// <c>Serilog:WriteTo</c> sinks can be attached to the step-up-gated output logger rather than the
    /// Verbose root (ADR 0005).
    /// <para>
    /// <c>Root</c> is the app config with every <c>Serilog:WriteTo:*</c> leaf removed, so the root reader
    /// attaches no output sinks but keeps <c>MinimumLevel</c> (incl. <c>Override</c>), <c>Enrich</c>,
    /// <c>Using</c>, <c>Properties</c>, <c>Filter</c> and <c>Destructure</c> exactly as before.
    /// </para>
    /// <para>
    /// <c>Gated</c> contains only <c>Serilog:WriteTo:*</c> and <c>Serilog:Using:*</c> leaves (Using lets
    /// Serilog resolve the declared sink assemblies); it carries no <c>MinimumLevel</c>/<c>Enrich</c>, so
    /// the output loggers stay Verbose and do not double-enrich.
    /// </para>
    /// </summary>
    internal static (IConfiguration Root, IConfiguration Gated) SplitSerilogConfiguration(IConfiguration appConfig)
    {
        // A path is "in" a section if it equals the section itself (so ancestor sections resolve) or
        // starts with "<section>:" (so a hypothetical sibling like "Serilog:WriteToFoo" doesn't match).
        // All comparisons are case-insensitive because configuration keys are.
        const string writeToSection = "Serilog:WriteTo";
        const string usingSection = "Serilog:Using";
        const string serilogSection = "Serilog";

        static bool InSubtree(string path, string section)
            => path.Equals(section, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase);

        bool RootVisible(string path) => !InSubtree(path, writeToSection);

        bool GatedVisible(string path)
            => InSubtree(path, writeToSection)
               || InSubtree(path, usingSection)
               || path.Equals(serilogSection, StringComparison.OrdinalIgnoreCase);

        var root = new PrefixFilteredConfiguration(appConfig, RootVisible);
        var gated = new PrefixFilteredConfiguration(appConfig, GatedVisible);
        return (root, gated);
    }

    /// <summary>
    /// Applies the root pipeline enrichers (ADR 0026). <c>FromLogContext</c>, <see cref="ThrowContextEnricher"/> and
    /// <see cref="AlwaysExportEnricher"/> run first and ungated, in that order: a <c>SourceContext</c> or marker pushed
    /// through <c>LogContext</c> must reach the always-export match and the gate, and the current context wins over
    /// the throw site's. Every other enricher runs only when
    /// <paramref name="needsEnrichment"/> accepts the event.
    /// </summary>
    internal void ApplyRootEnrichers(LoggerConfiguration lc, Func<LogEvent, bool> needsEnrichment)
    {
        lc.Enrich.FromLogContext();

        if (Options.CaptureScopesOnException)
        {
            lc.Enrich.With(new ThrowContextEnricher());
        }

        if (settings.AlwaysExportCategories.Length > 0)
        {
            lc.Enrich.With(new AlwaysExportEnricher(settings.AlwaysExportCategories));
        }

        lc.Enrich.When(needsEnrichment, ApplyCommonEnrichers);
    }

    /// <summary>
    /// Registers the property redaction enricher (ADR 0022 D5) behind <paramref name="needsEnrichment"/>: an event the
    /// gate drops is exported by no sink, so sweeping its properties would be wasted work. With no pattern to apply
    /// the enricher would walk every event's properties to no effect, so the flag alone does not earn the sweep; the
    /// pattern array is fixed at startup, so this decision cannot go stale.
    /// </summary>
    internal void ApplyRedactionEnricher(LoggerConfiguration lc, Func<LogEvent, bool> needsEnrichment)
    {
        if (!Options.RedactLogEventProperties || patterns.Patterns.Length == 0) return;
        lc.Enrich.When(needsEnrichment, enrich => enrich.With(new RedactionEnricher(patterns)));
    }

    /// <summary>
    /// Applies all configured enrichers to <paramref name="enrich"/>. Called on both the root
    /// pipeline and the bypass logger to keep enrichment consistent. <c>FromLogContext</c> is not part of it: the
    /// root applies it ungated, the bypass logger applies it first. Each enricher is its own call on
    /// <paramref name="enrich"/>: a chained <c>.Enrich.</c> inside <c>When</c> would reach the logger
    /// configuration and escape the gate.
    /// </summary>
    private void ApplyCommonEnrichers(LoggerEnrichmentConfiguration enrich)
    {
        if (patterns.Patterns.Length > 0)
        {
            enrich.With(new PathPropertyRedactionEnricher(patterns));
        }

        enrich.WithOpenTelemetryTraceId();
        enrich.WithOpenTelemetrySpanId();
        enrich.With<ActivityContextEnricher>();
        enrich.WithProperty("Application", builder.Environment.ApplicationName);

        foreach (var add in OptionalEnrichers())
        {
            add(enrich);
        }
    }

    private IEnumerable<Action<LoggerEnrichmentConfiguration>> OptionalEnrichers()
    {
        var opts = Options;
        (bool On, Action<LoggerEnrichmentConfiguration> Add)[] table =
        [
            (opts.EnrichWithExceptionDetails && opts.StructuredExceptionDetails, e => e.WithExceptionDetails()),
            (opts.EnrichWithEnvironment, e => e.WithProperty("Environment", builder.Environment.EnvironmentName)),
            (opts.EnrichWithThreadId, e => e.WithThreadId()),
            (opts.EnrichWithProcessId, e => e.WithProcessId()),
            (opts.EnrichWithMachineName, e => e.WithMachineName()),
            (opts.EnrichWithCallStack, e => e.WithCallStack()),
            (!string.IsNullOrWhiteSpace(opts.ServiceVersion), e => e.WithProperty("ServiceVersion", (object?)opts.ServiceVersion)),
            (!string.IsNullOrWhiteSpace(opts.ServiceInstanceId), e => e.WithProperty("ServiceInstanceId", (object?)opts.ServiceInstanceId)),
        ];
        return table.Where(row => row.On).Select(row => row.Add);
    }

    /// <summary>
    /// The output logger behind the step-up gate. Config-declared <c>Serilog:WriteTo</c> sinks join the library's
    /// own; it has no enrichers, since events arrive enriched from the root.
    /// </summary>
    private ILogger CreateGatedLogger(IConfiguration gatedConfig)
        => WithOutputs(new LoggerConfiguration().MinimumLevel.Verbose(), gatedConfig).CreateLogger();

    /// <summary>
    /// The bypass logger: exports immediate events, request summaries and pre-error buffer flushes independently of
    /// the step-up switch, to the same outputs as the gated logger, so they reach config-declared sinks even with
    /// the OTLP exporter off.
    /// </summary>
    /// <remarks>
    /// A config-declared File sink attaches to both output loggers. Without <c>shared:true</c> the two writers
    /// contend for the file lock and one silently drops its output (ADR 0003).
    /// </remarks>
    private ILogger CreateBypassLogger(IConfiguration gatedConfig)
    {
        var cfg = new LoggerConfiguration().MinimumLevel.Verbose();
        cfg.Enrich.FromLogContext();
        ApplyCommonEnrichers(cfg.Enrich);
        return WithOutputs(cfg, gatedConfig).CreateLogger();
    }

    private LoggerConfiguration WithOutputs(LoggerConfiguration cfg, IConfiguration gatedConfig)
    {
        SerilogConfigurationAssemblies.ReadSerilogSection(cfg, gatedConfig);
        OutputSinks.WriteTo(cfg, Options, builder.Environment.ContentRootPath, logFilePath);
        return cfg;
    }
}
