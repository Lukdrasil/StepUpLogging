using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The entry points of StepUp logging: <c>AddStepUpLogging</c> and <c>AddAuditLogging</c> on the host builder,
/// <c>UseStepUpRequestLogging</c> on the app, and the activity source and meter names to subscribe to.
/// </summary>
public static class StepUpLoggingExtensions
{
    /// <summary>ActivitySource name for request logging (LogRequest, ApplyRedaction, CaptureRequestBody), for explicit registration.</summary>
    public const string RequestLoggingActivitySourceName = "Lukdrasil.StepUpLogging.RequestLogging";

    /// <summary>ActivitySource name for controller state transitions (TriggerStepUp, PerformStepDown), for explicit registration.</summary>
    public const string ControllerActivitySourceName = "Lukdrasil.StepUpLogging.Controller";

    /// <summary>ActivitySource name for pre-error buffer flushes (FlushBufferedEvents), for explicit registration.</summary>
    public const string BufferActivitySourceName = "Lukdrasil.StepUpLogging.Buffer";

    /// <summary>
    /// Adds StepUp logging with OpenTelemetry as the primary export mechanism.
    /// Configuration is loaded from appsettings.json section (default: "SerilogStepUp").
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configureOptions">Action to configure StepUpLoggingOptions (enable console output via <see cref="StepUpLoggingOptions.EnableConsoleLogging"/>)</param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
    /// <remarks>
    /// Serilog's static <c>Log.Logger</c> is left unchanged unless <see cref="StepUpLoggingOptions.SetStaticLogger"/>
    /// is true in the configuration section or in <paramref name="configureOptions"/>.
    /// </remarks>
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions = null,
        string configSectionName = "SerilogStepUp",
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, configureOptions, null, configSectionName, logFilePath);
    }

    /// <summary>
    /// Adds StepUp logging with OpenTelemetry as the primary export mechanism.
    /// Configuration is loaded from appsettings.json section (default: "SerilogStepUp").
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configure">Optional additional Serilog configuration; receives the resolved <see cref="IServiceProvider"/> (use it to resolve <see cref="IConfiguration"/>/<see cref="IHostEnvironment"/> if needed) and the root <see cref="LoggerConfiguration"/></param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
    /// <remarks>
    /// Serilog's static <c>Log.Logger</c> is left unchanged unless <see cref="StepUpLoggingOptions.SetStaticLogger"/>
    /// is true in the configuration section.
    /// </remarks>
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName = "SerilogStepUp",
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, null, configure, configSectionName, logFilePath);
    }

    /// <summary>
    /// Adds StepUp logging with OpenTelemetry as the primary export mechanism, taking both an options callback and a Serilog configuration callback.
    /// Configuration is loaded from appsettings.json section (default: "SerilogStepUp").
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configureOptions">Action to configure StepUpLoggingOptions, applied after the configuration section is bound</param>
    /// <param name="configure">Additional Serilog configuration; receives the resolved <see cref="IServiceProvider"/> and the root <see cref="LoggerConfiguration"/></param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
    /// <remarks>
    /// Serilog's static <c>Log.Logger</c> is left unchanged unless <see cref="StepUpLoggingOptions.SetStaticLogger"/>
    /// is true in the configuration section or in <paramref name="configureOptions"/>.
    /// </remarks>
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName = "SerilogStepUp",
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, configureOptions, configure, configSectionName, logFilePath);
    }

    /// <summary>
    /// Adds audit logging: <typeparamref name="TSink"/> becomes the <see cref="IAuditEventSink"/>
    /// that every <see cref="IAuditLogger{T}"/> writes to. Requires <see cref="AddStepUpLogging(IHostApplicationBuilder, Action{StepUpLoggingOptions}?, string, string?)"/>.
    /// </summary>
    /// <remarks>
    /// The package ships no sink: there is deliberately no default and no no-op implementation to
    /// fall back on, because an audit trail that silently goes nowhere is worse than none at all.
    /// Auditing is turned off by not calling this method — there is no configuration flag for it.
    /// <para>
    /// The guard catches anything already registered when this method runs, however it got there —
    /// including a prior direct <c>services.AddSingleton&lt;IAuditEventSink, MySink&gt;()</c>. What it
    /// cannot see is a registration added <em>after</em> this method returns: a later direct
    /// <c>AddSingleton</c> still silently keeps only the last sink (ADR 0018 D5's consequences).
    /// Policing registrations this method does not own would mean inspecting the container on every
    /// resolve, not just here, so that case is documented rather than chased.
    /// </para>
    /// </remarks>
    /// <typeparam name="TSink">The consumer's sink, which owns the audit store and its transaction.</typeparam>
    /// <param name="builder">The host application builder</param>
    /// <param name="sinkLifetime">Lifetime of <typeparamref name="TSink"/> (default: <see cref="ServiceLifetime.Scoped"/>, so the sink can share the request's <c>DbContext</c>)</param>
    /// <exception cref="InvalidOperationException">
    /// An <see cref="IAuditEventSink"/> is already registered. <see cref="IAuditLogger{T}"/> resolves a
    /// single sink, so a second registration would silently discard one of them (ADR 0018 D5). Naming a
    /// test sink in <c>ConfigureTestServices</c> hits this too, since it registers before the host is
    /// built, on the same collection Program.cs already registered on — call
    /// <c>services.RemoveAll&lt;IAuditEventSink&gt;()</c> first to substitute a test double.
    /// </exception>
    public static IHostApplicationBuilder AddAuditLogging<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSink>(
        this IHostApplicationBuilder builder,
        ServiceLifetime sinkLifetime = ServiceLifetime.Scoped)
        where TSink : class, IAuditEventSink
    {
        var existingSinkRegistration = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(IAuditEventSink));
        if (existingSinkRegistration is not null)
        {
            var existingSinkName = existingSinkRegistration.ImplementationType?.Name ?? "an existing IAuditEventSink";
            throw new InvalidOperationException(
                $"AddAuditLogging<{typeof(TSink).Name}> failed: {existingSinkName} is already registered as the " +
                "IAuditEventSink. AuditLogger<T> resolves a single sink, so registering a second one would " +
                "silently discard one of them. If this is a test substituting a sink Program.cs already " +
                $"registered, call services.{nameof(ServiceCollectionDescriptorExtensions.RemoveAll)}<{nameof(IAuditEventSink)}>() before registering the test sink.");
        }

        // Audit records must carry where the action came from, so the accessor is a deliberate
        // dependency rather than an optional one.
        builder.Services.AddHttpContextAccessor();
        builder.Services.Add(new ServiceDescriptor(typeof(IAuditEventSink), typeof(TSink), sinkLifetime));
        // Scoped regardless of the sink's lifetime: the audit logger reads per-request context.
        builder.Services.AddScoped<AuditTrail>();
        builder.Services.AddScoped(typeof(IAuditLogger<>), typeof(AuditLogger<>));

        // The prerequisite is checked through the service provider rather than through
        // builder.Services, so the two registration calls stay valid in either order (ADR 0007
        // start-up validation, as for StepUpLoggingOptions).
        builder.Services.AddOptions<AuditLoggingPrerequisites>()
            .Validate<IServiceProvider>(
                (_, services) => IsStepUpLoggingRegistered(services),
                "AddAuditLogging requires AddStepUpLogging: audit records resolve the client IP with the same rule as request logging (ADR 0008), and only AddStepUpLogging registers it. Add builder.AddStepUpLogging() in Program.cs — the order of the two calls does not matter.")
            .ValidateOnStart();

        return builder;
    }

    private static bool IsStepUpLoggingRegistered(IServiceProvider services)
        => services.GetService<StepUpSettings>() is not null && services.GetService<CompiledRedactionPatterns>() is not null;

    private static IHostApplicationBuilder AddStepUpLoggingInternal(
        IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName,
        string? logFilePath)
    {
        var optionsSection = builder.Configuration.GetSection(configSectionName);
        var atRegistration = ReadAtRegistration(optionsSection, configureOptions);
        if (atRegistration.EnableOtlpExporter)
        {
            OtlpExport.Register(builder.Services);
        }

        // Standard options pipeline: bind the section, apply the caller's overrides, then validate at
        // startup so a misconfiguration (invalid level string, non-positive duration) fails fast rather
        // than silently degrading to a fallback (ADR 0007).
        builder.Services.AddOptions<StepUpLoggingOptions>()
            .Bind(optionsSection)
            .Configure(options => configureOptions?.Invoke(options))
            .PostConfigure(StepUpSettings.ApplyDefaults)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<StepUpLoggingOptions>>(new StepUpLoggingOptionsValidator(configSectionName)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, OutboundRequestSummaryFilter>());

        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddStepUpLoggingMeters());

        builder.Services.AddSingleton(sp => new StepUpSettings(sp.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value));
        builder.Services.AddSingleton(sp => CompiledRedactionPatterns.From(sp.GetRequiredService<StepUpSettings>().Options));
        builder.Services.AddSingleton(sp => new StepUpLoggingController(
            sp.GetRequiredService<StepUpSettings>(), null, sp.GetService<TimeProvider>() ?? TimeProvider.System));

        builder.Services.AddSerilog((services, lc) =>
            new StepUpPipeline(builder, services.GetRequiredService<StepUpSettings>(), services.GetRequiredService<CompiledRedactionPatterns>(), logFilePath)
                .Configure(lc, services.GetRequiredService<StepUpLoggingController>(), services, configure, optionsSection),
            preserveStaticLogger: !atRegistration.SetStaticLogger, writeToProviders: false);

        return builder;
    }

    /// <summary>
    /// The options for the two choices made while services are registered, before <c>IOptions</c> can be resolved:
    /// whether OTLP export is registered and whether <c>AddSerilog</c> replaces <c>Log.Logger</c>. It binds the same
    /// section and runs the same overrides as the options pipeline. Every other option is read at runtime through
    /// <see cref="StepUpSettings"/>.
    /// </summary>
    private static StepUpLoggingOptions ReadAtRegistration(IConfigurationSection section, Action<StepUpLoggingOptions>? configureOptions)
    {
        var options = new StepUpLoggingOptions();
        section.Bind(options);
        configureOptions?.Invoke(options);
        return options;
    }

    /// <summary>Adds the library's meters to <paramref name="builder"/>; <c>AddStepUpLogging</c> already does it for the host's meter provider.</summary>
    public static MeterProviderBuilder AddStepUpLoggingMeters(this MeterProviderBuilder builder)
    {
        return builder
            .AddMeter("StepUpLogging")
            .AddMeter("StepUpLogging.Sink")
            .AddMeter("StepUpLogging.RequestLogging")
            .AddMeter("StepUpLogging.Buffer")
            .AddMeter("StepUpLogging.Immediate")
            .AddMeter("StepUpLogging.Audit");
    }

    /// <summary>
    /// Adds Serilog request logging middleware with enriched context (route parameters, headers, body capture).
    /// Integrates with StepUp logging to capture detailed request information when logging is stepped-up (on errors).
    /// Request completion events go to the DI <see cref="Serilog.ILogger"/>, not to the static <c>Log.Logger</c>.
    /// </summary>
    public static IApplicationBuilder UseStepUpRequestLogging(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        new StepUpRequestLogging(services.GetRequiredService<StepUpSettings>(), services.GetRequiredService<StepUpLoggingController>(), services.GetRequiredService<CompiledRedactionPatterns>())
            .UseIn(app, services.GetService<Serilog.ILogger>());
        return app;
    }

    /// <summary>
    /// Adds Serilog request logging middleware. Convenience overload for <see cref="WebApplication"/>.
    /// </summary>
    public static WebApplication UseStepUpRequestLogging(this WebApplication app)
    {
        ((IApplicationBuilder)app).UseStepUpRequestLogging();
        return app;
    }
}

/// <summary>
/// Carries nothing; it exists only so that the prerequisites of
/// <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/> can be checked by the options
/// pipeline's <c>ValidateOnStart</c>, which is what makes the host refuse to start.
/// </summary>
internal sealed class AuditLoggingPrerequisites;
