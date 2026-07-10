using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Serilog.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.OpenTelemetry;
using Serilog.Formatting.Compact;
using Serilog.Events;
using Serilog.Sinks.File;
using Serilog.Sinks.OpenTelemetry;
using Serilog.Enrichers.CallStack;

namespace Lukdrasil.StepUpLogging;

public static class StepUpLoggingExtensions
{
    /// <summary>
    /// ActivitySource for request logging operations (instrumentation can be disabled via EnableActivityInstrumentation).
    /// Activities:
    /// - LogRequest: Main request processing
    /// - CaptureRequestBody: Body capture for POST/PUT/PATCH
    /// - ApplyRedaction: Sensitive data redaction operations
    /// </summary>
    public static readonly ActivitySource RequestLoggingActivitySource = new("Lukdrasil.StepUpLogging.RequestLogging", "1.0.0");

    /// <summary>
    /// ActivitySource for controller state transitions.
    /// Activities:
    /// - TriggerStepUp: When logging level steps up on error
    /// - PerformStepDown: When logging level steps down after timeout
    /// </summary>
    public static readonly ActivitySource ControllerActivitySource = new("Lukdrasil.StepUpLogging.Controller", "1.0.0");

    /// <summary>
    /// ActivitySource for buffer operations.
    /// Activities:
    /// - FlushBufferedEvents: When buffer is flushed due to error
    /// - BufferEvent: Individual event buffering (use sparingly)
    /// </summary>
    public static readonly ActivitySource BufferActivitySource = new("Lukdrasil.StepUpLogging.Buffer", "1.0.0");

    /// <summary>ActivitySource name for request logging (for explicit registration).</summary>
    public const string RequestLoggingActivitySourceName = "Lukdrasil.StepUpLogging.RequestLogging";

    /// <summary>ActivitySource name for controller operations (for explicit registration).</summary>
    public const string ControllerActivitySourceName = "Lukdrasil.StepUpLogging.Controller";

    /// <summary>ActivitySource name for buffer operations (for explicit registration).</summary>
    public const string BufferActivitySourceName = "Lukdrasil.StepUpLogging.Buffer";

    private static readonly Meter RequestMeter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> BodyCaptureCounter = RequestMeter.CreateCounter<long>("request_body_captured_total", "count", "Number of requests with captured body");
    private static readonly Counter<long> RedactionCounter = RequestMeter.CreateCounter<long>("request_redaction_applied_total", "count", "Number of requests with redaction applied");

    // Built-in sensitive headers (static to avoid allocation on every request)
    private static readonly HashSet<string> BuiltInSensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "X-API-Key", "X-Auth-Token", "X-Access-Token",
        "Authorization-Token", "Proxy-Authorization", "WWW-Authenticate", "Sec-WebSocket-Key"
    };

    /// <summary>
    /// Adds StepUp logging with OpenTelemetry as the primary export mechanism.
    /// Configuration is loaded from appsettings.json section (default: "SerilogStepUp").
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configureOptions">Action to configure StepUpLoggingOptions (enable console output via <see cref="StepUpLoggingOptions.EnableConsoleLogging"/>)</param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
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
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName = "SerilogStepUp",
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, null, configure, configSectionName, logFilePath);
    }

    private static IHostApplicationBuilder AddStepUpLoggingInternal(
        IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName,
        string? logFilePath)
    {
        var configSnapshot = new StepUpLoggingOptions();
        builder.Configuration.GetSection(configSectionName).Bind(configSnapshot);

        if (configSnapshot.EnableOtlpExporter && !IsOpenTelemetryRegistered(builder.Services))
        {
            // Align with Aspire defaults: ensure OTLP exporter is registered when OTLP logging is enabled
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Standard options pipeline: bind the section, apply the caller's overrides, then validate at
        // startup so a misconfiguration (invalid level string, non-positive duration) fails fast rather
        // than silently degrading to a fallback (ADR 0007).
        builder.Services.AddOptions<StepUpLoggingOptions>()
            .Bind(builder.Configuration.GetSection(configSectionName))
            .Configure(options => configureOptions?.Invoke(options))
            .Validate(ValidateOptions, "Invalid SerilogStepUp options: DurationSeconds must be > 0 and BaseLevel/StepUpLevel/RequestSummaryLevel must be valid Serilog levels.")
            .ValidateOnStart();

        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddStepUpLoggingMeters());

        builder.Services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
            var patterns = opts.RedactionRegexes
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                .ToArray();
            return new CompiledRedactionPatterns(patterns);
        });

        builder.Services.AddSingleton<StepUpLoggingController>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
            return new StepUpLoggingController(opts);
        });

        builder.Services.AddSerilog((services, lc) =>
        {
            var stepUpController = services.GetRequiredService<StepUpLoggingController>();
            var opts = services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

            // Split configuration so user-declared Serilog:WriteTo sinks attach to the gated inner
            // logger (behind the LevelSwitch) instead of the Verbose root — otherwise they would
            // export everything at Verbose and bypass step-up entirely (ADR 0005). The root still
            // reads MinimumLevel/Enrich/Using/etc. so the buffer/trigger sinks and enrichment are
            // unchanged.
            var (rootConfig, gatedConfig) = SplitSerilogConfiguration(builder.Configuration);

            lc.ReadFrom.Configuration(rootConfig)
              .MinimumLevel.Verbose();

            ApplyCommonEnrichers(lc, builder, opts);

            // Bypass logger: exports at full verbosity independent of LevelSwitch.
            // Built directly here (not via DI) to avoid a circular deadlock:
            // AddSerilog registers Serilog.ILogger as a factory that depends on ILoggerFactory,
            // which in turn depends on this very callback — resolving it inside the callback deadlocks.
            var bypassLogger = CreateBypassLogger(builder, logFilePath, opts);
            try { stepUpController.SetSummaryLogger(bypassLogger); } catch { }

            // Step-up sink: gated by LevelSwitch, drops bypass-marked events to prevent duplication.
            var stepUpInnerCfg = new LoggerConfiguration();
            // Config-declared WriteTo sinks join the library's own output sinks behind the LevelSwitch.
            // gatedConfig carries only WriteTo + Using (no MinimumLevel/Enrich), so the inner logger
            // stays Verbose and does not double-enrich (events arrive already enriched from the root).
            stepUpInnerCfg.ReadFrom.Configuration(gatedConfig);
            ConfigureOutputSinks(stepUpInnerCfg, builder, logFilePath, opts);
            stepUpInnerCfg.MinimumLevel.Verbose();
            lc.WriteTo.Sink(new StepUpSink(
                stepUpInnerCfg.CreateLogger(),
                stepUpController.LevelSwitch,
                stepUpController.BaseLevel,
                []));

            // Pre-error buffer: captures all events per trace; flushes to bypass logger on Error/Fatal.
            if (opts.EnablePreErrorBuffering)
            {
                lc.WriteTo.Sink(new PreErrorBufferSink(bypassLogger, opts.PreErrorBufferSize, opts.PreErrorMaxContexts));
            }

            // Trigger sink: observes Error/Fatal events and calls controller.Trigger() asynchronously.
            lc.WriteTo.Sink(new StepUpTriggerSink(stepUpController));

            // Summary sink: routes IsRequestSummary=true events to bypass logger.
            lc.WriteTo.Sink(new SummarySink(bypassLogger));

            // Immediate sink: routes IsImmediate=true events to bypass logger.
            lc.WriteTo.Sink(new ImmediateSink(bypassLogger));

            configure?.Invoke(services, lc);
        }, writeToProviders: false);

        return builder;
    }

    /// <summary>
    /// Partitions the application configuration into two Serilog views so that config-declared
    /// <c>Serilog:WriteTo</c> sinks can be attached to the step-up-gated inner logger rather than the
    /// Verbose root (ADR 0005).
    /// <para>
    /// <c>Root</c> is the app config with every <c>Serilog:WriteTo:*</c> leaf removed, so the root reader
    /// attaches no output sinks but keeps <c>MinimumLevel</c> (incl. <c>Override</c>), <c>Enrich</c>,
    /// <c>Using</c>, <c>Properties</c>, <c>Filter</c> and <c>Destructure</c> exactly as before.
    /// </para>
    /// <para>
    /// <c>Gated</c> contains only <c>Serilog:WriteTo:*</c> and <c>Serilog:Using:*</c> leaves (Using lets
    /// Serilog resolve the declared sink assemblies); it carries no <c>MinimumLevel</c>/<c>Enrich</c>, so
    /// the inner logger stays Verbose and does not double-enrich.
    /// </para>
    /// </summary>
    internal static (IConfiguration Root, IConfiguration Gated) SplitSerilogConfiguration(IConfiguration appConfig)
    {
        // Trailing colon so the prefix matches only the WriteTo/Using arrays themselves, never a
        // hypothetical sibling like "Serilog:WriteToFoo".
        const string writeToPrefix = "Serilog:WriteTo:";
        const string usingPrefix = "Serilog:Using:";

        var rootPairs = new List<KeyValuePair<string, string?>>();
        var gatedPairs = new List<KeyValuePair<string, string?>>();

        foreach (var kvp in appConfig.AsEnumerable())
        {
            // AsEnumerable() yields intermediate section nodes with null values; only leaves carry values.
            if (kvp.Value is null) continue;

            var isWriteTo = kvp.Key.StartsWith(writeToPrefix, StringComparison.OrdinalIgnoreCase);
            var isUsing = kvp.Key.StartsWith(usingPrefix, StringComparison.OrdinalIgnoreCase);

            if (!isWriteTo)
            {
                rootPairs.Add(kvp);
            }

            if (isWriteTo || isUsing)
            {
                gatedPairs.Add(kvp);
            }
        }

        var root = new ConfigurationBuilder().AddInMemoryCollection(rootPairs).Build();
        var gated = new ConfigurationBuilder().AddInMemoryCollection(gatedPairs).Build();
        return (root, gated);
    }

    private static LogEventLevel ParseLogEventLevel(string? value, LogEventLevel fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<LogEventLevel>(value, true, out var lvl) ? lvl : fallback;
    }

    /// <summary>
    /// Validates that <paramref name="o"/> is coherent: a positive step-up duration and level strings
    /// that parse to a Serilog <see cref="LogEventLevel"/>. Used by the options pipeline at startup so a
    /// typo'd level or a non-positive duration surfaces immediately instead of silently falling back.
    /// </summary>
    private static bool ValidateOptions(StepUpLoggingOptions o)
        => o.DurationSeconds > 0
           && IsValidLevel(o.BaseLevel)
           && IsValidLevel(o.StepUpLevel)
           && IsValidLevel(o.RequestSummaryLevel);

    private static bool IsValidLevel(string? value)
        => !string.IsNullOrWhiteSpace(value) && Enum.TryParse<LogEventLevel>(value, true, out _);

    /// <summary>
    /// Applies all configured enrichers to <paramref name="lc"/>. Called on both the root
    /// pipeline and the bypass logger to keep enrichment consistent.
    /// </summary>
    private static void ApplyCommonEnrichers(LoggerConfiguration lc, IHostApplicationBuilder builder, StepUpLoggingOptions opts)
    {
        lc.Enrich.FromLogContext()
          .Enrich.WithOpenTelemetryTraceId()
          .Enrich.WithOpenTelemetrySpanId()
          .Enrich.With<ActivityContextEnricher>()
          .Enrich.WithProperty("Application", builder.Environment.ApplicationName);

        if (opts.EnrichWithExceptionDetails && opts.StructuredExceptionDetails)
        {
            lc.Enrich.WithExceptionDetails();
        }

        if (opts.EnrichWithEnvironment)
        {
            lc.Enrich.WithProperty("Environment", builder.Environment.EnvironmentName);
        }

        if (opts.EnrichWithThreadId)
        {
            lc.Enrich.WithThreadId();
        }

        if (opts.EnrichWithProcessId)
        {
            lc.Enrich.WithProcessId();
        }

        if (opts.EnrichWithMachineName)
        {
            lc.Enrich.WithMachineName();
        }

        if (opts.EnrichWithCallStack)
        {
            lc.Enrich.WithCallStack();
        }

        if (!string.IsNullOrWhiteSpace(opts.ServiceVersion))
        {
            lc.Enrich.WithProperty("ServiceVersion", opts.ServiceVersion);
        }

        if (!string.IsNullOrWhiteSpace(opts.ServiceInstanceId))
        {
            lc.Enrich.WithProperty("ServiceInstanceId", opts.ServiceInstanceId);
        }
    }

    /// <summary>
    /// Creates the bypass/summary logger that exports events independently of the main LevelSwitch.
    /// </summary>
    private static Serilog.ILogger CreateBypassLogger(
        IHostApplicationBuilder builder,
        string? logFilePath,
        StepUpLoggingOptions opts)
    {
        var cfg = new LoggerConfiguration().MinimumLevel.Verbose();
        ApplyCommonEnrichers(cfg, builder, opts);
        ConfigureOutputSinks(cfg, builder, logFilePath, opts);
        return cfg.CreateLogger();
    }

    public static MeterProviderBuilder AddStepUpLoggingMeters(this MeterProviderBuilder builder)
    {
        return builder
            .AddMeter("StepUpLogging")
            .AddMeter("StepUpLogging.Sink")
            .AddMeter("StepUpLogging.RequestLogging")
            .AddMeter("StepUpLogging.Buffer")
            .AddMeter("StepUpLogging.Immediate");
    }

    /// <summary>
    /// Adds Serilog request logging middleware with enriched context (route parameters, headers, body capture).
    /// Integrates with StepUp logging to capture detailed request information when logging is stepped-up (on errors).
    /// </summary>
    public static IApplicationBuilder UseStepUpRequestLogging(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var opts = services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
        var stepUpController = services.GetRequiredService<StepUpLoggingController>();
        var compiledPatterns = services.GetRequiredService<CompiledRedactionPatterns>();
        var env = services.GetRequiredService<IHostEnvironment>();

        var exclude = new HashSet<string>(opts.ExcludePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var excludePrefixes = (opts.ExcludePaths ?? Array.Empty<string>())
            .Where(p => p.EndsWith("*", StringComparison.Ordinal))
            .Select(p => p.TrimEnd('*').TrimEnd('/'))
            .ToArray();

        var sensitiveHeaders = new HashSet<string>(BuiltInSensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        if (opts.AdditionalSensitiveHeaders?.Length > 0)
        {
            foreach (var header in opts.AdditionalSensitiveHeaders)
            {
                sensitiveHeaders.Add(header);
            }
        }

        if (opts.AlwaysLogRequestSummary)
        {
            app.Use(async (httpContext, next) =>
            {
                var sw = Stopwatch.StartNew();
                var failed = false;
                try
                {
                    await next();
                }
                catch
                {
                    // An unhandled exception must not swallow the request summary — the failing
                    // requests are the ones worth summarizing. Emit with 500, then rethrow.
                    failed = true;
                    throw;
                }
                finally
                {
                    sw.Stop();

                    var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                    var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath;

                    if (!exclude.Contains(path) && !excludePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var qs = httpContext.Request.QueryString.HasValue ? httpContext.Request.QueryString.Value! : string.Empty;
                            var redactedQs = compiledPatterns.Redact(qs);

                            IReadOnlyDictionary<string, object?>? routeParams = null;
                            if (httpContext.Request.RouteValues?.Count > 0)
                            {
                                var rp = new Dictionary<string, object?>();
                                foreach (var kvp in httpContext.Request.RouteValues)
                                {
                                    var value = kvp.Value?.ToString() ?? string.Empty;
                                    rp[kvp.Key] = compiledPatterns.Redact(value);
                                }
                                routeParams = rp;
                            }

                            var userAgent = ExtractUserAgent(httpContext.Request);
                            var clientIp = ExtractClientIp(httpContext);
                            var jti = httpContext.User?.FindFirst("jti")?.Value;
                            var statusCode = failed ? StatusCodes.Status500InternalServerError : (httpContext.Response?.StatusCode ?? 0);
                            stepUpController.EmitRequestSummary(
                                httpContext.Request.Method,
                                path,
                                statusCode,
                                sw.Elapsed.TotalMilliseconds,
                                Activity.Current?.TraceId.ToString(),
                                redactedQs,
                                routeParams,
                                userAgent,
                                clientIp,
                                jti);
                        }
                        catch
                        {
                            // Never let summary emission mask the original request outcome.
                        }
                    }
                }
            });
        }

        if (opts.CaptureRequestBody)
        {
            // Enable request buffering BEFORE the endpoint reads the body, so the enricher can rewind
            // and re-read it at request completion. Doing this inside the enricher (after the pipeline)
            // was too late: the body had already been consumed from a non-buffered stream (ADR 0004).
            app.Use(async (httpContext, next) =>
            {
                var method = httpContext.Request.Method;
                if (method is "POST" or "PUT" or "PATCH")
                {
                    httpContext.Request.EnableBuffering();
                }
                await next();
            });
        }

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                using var activity = opts.EnableActivityInstrumentation
                    ? RequestLoggingActivitySource.StartActivity("LogRequest", ActivityKind.Server)
                    : null;

                activity?.SetTag("http.method", httpContext.Request.Method);
                activity?.SetTag("http.target", httpContext.Request.Path.Value);
                activity?.SetTag("http.scheme", httpContext.Request.Scheme);
                activity?.SetTag("http.host", httpContext.Request.Host.Value);

                var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath;
                diagnosticContext.Set("RequestPath", path);

                var qs = httpContext.Request.QueryString.HasValue ? httpContext.Request.QueryString.Value! : string.Empty;
                var redactedQs = compiledPatterns.Redact(qs);
                if (!string.Equals(redactedQs, qs, StringComparison.Ordinal))
                {
                    if (opts.EnableActivityInstrumentation)
                    {
                        using (var redactionActivity = RequestLoggingActivitySource.StartActivity("ApplyRedaction", ActivityKind.Internal))
                        {
                            redactionActivity?.SetTag("security.redaction_type", "query_string");
                            redactionActivity?.SetTag("security.redaction_target", "query_string");
                        }
                    }
                    RedactionCounter.Add(1);
                    activity?.SetTag("security.redaction_applied", true);
                }
                diagnosticContext.Set("QueryString", redactedQs);

                if (httpContext.Request.RouteValues?.Count > 0)
                {
                    var routeParams = new Dictionary<string, object?>();
                    foreach (var kvp in httpContext.Request.RouteValues)
                    {
                        var value = kvp.Value?.ToString() ?? string.Empty;
                        var redactedValue = compiledPatterns.Redact(value);
                        if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
                        {
                            if (opts.EnableActivityInstrumentation)
                            {
                                using (var redactionActivity = RequestLoggingActivitySource.StartActivity("ApplyRedaction", ActivityKind.Internal))
                                {
                                    redactionActivity?.SetTag("security.redaction_type", "route_parameter");
                                    redactionActivity?.SetTag("security.redaction_target", kvp.Key);
                                }
                            }
                            RedactionCounter.Add(1);
                        }
                        routeParams[kvp.Key] = redactedValue;
                    }
                    diagnosticContext.Set("RouteParameters", routeParams);
                }

                var headers = new Dictionary<string, object?>();
                foreach (var header in httpContext.Request.Headers)
                {
                    if (sensitiveHeaders.Contains(header.Key))
                    {
                        headers[header.Key] = "[REDACTED]";
                        if (opts.EnableActivityInstrumentation)
                        {
                            using (var redactionActivity = RequestLoggingActivitySource.StartActivity("ApplyRedaction", ActivityKind.Internal))
                            {
                                redactionActivity?.SetTag("security.redaction_type", "sensitive_header");
                                redactionActivity?.SetTag("security.redaction_target", header.Key);
                            }
                        }
                        RedactionCounter.Add(1);
                    }
                    else
                    {
                        var value = string.Join(", ", header.Value.Where(v => v != null) ?? Array.Empty<string>());
                        var redactedValue = compiledPatterns.Redact(value);
                        if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
                        {
                            if (opts.EnableActivityInstrumentation)
                            {
                                using (var redactionActivity = RequestLoggingActivitySource.StartActivity("ApplyRedaction", ActivityKind.Internal))
                                {
                                    redactionActivity?.SetTag("security.redaction_type", "pattern");
                                    redactionActivity?.SetTag("security.redaction_target", header.Key);
                                }
                            }
                            RedactionCounter.Add(1);
                        }
                        headers[header.Key] = redactedValue;
                    }
                }
                diagnosticContext.Set("Headers", headers);

                var jtiClaim = httpContext.User?.FindFirst("jti")?.Value;
                if (!string.IsNullOrEmpty(jtiClaim))
                    diagnosticContext.Set("Jti", jtiClaim);

                if (opts.CaptureRequestBody && stepUpController.IsSteppedUp)
                {
                    var method = httpContext.Request.Method;
                    // Body.CanSeek is true only when the early buffering middleware ran for this request.
                    if ((method is "POST" or "PUT" or "PATCH") && httpContext.Request.Body.CanSeek)
                    {
                        try
                        {
                            using (opts.EnableActivityInstrumentation
                                ? RequestLoggingActivitySource.StartActivity("CaptureRequestBody", ActivityKind.Internal)
                                : null)
                            {
                                // The enricher is synchronous; Kestrel forbids sync reads by default, so opt
                                // this request in. The body is a rewindable buffered stream at this point.
                                var bodyControl = httpContext.Features.Get<IHttpBodyControlFeature>();
                                if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

                                httpContext.Request.Body.Position = 0;
                                var maxBytes = opts.MaxBodyCaptureBytes;
                                var buffer = new char[maxBytes];
                                using var sr = new StreamReader(httpContext.Request.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                                var read = sr.Read(buffer, 0, maxBytes);
                                httpContext.Request.Body.Position = 0;

                                if (read > 0)
                                {
                                    diagnosticContext.Set("RequestBody", compiledPatterns.Redact(new string(buffer, 0, read)));
                                    BodyCaptureCounter.Add(1);
                                }
                                else
                                {
                                    diagnosticContext.Set("RequestBody", "[EMPTY]");
                                }
                            }
                        }
                        catch
                        {
                            diagnosticContext.Set("RequestBody", "[UNAVAILABLE]");
                        }
                    }
                }
            };

            options.GetLevel = (httpContext, elapsed, ex) =>
            {
                var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath;
                if (exclude.Contains(path) || excludePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return LogEventLevel.Verbose;
                }

                if (ex != null) return LogEventLevel.Error;
                var status = httpContext.Response?.StatusCode ?? 0;
                if (status >= 500) return LogEventLevel.Error;
                if (status >= 400) return LogEventLevel.Warning;
                return LogEventLevel.Information;
            };
        });

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

    /// <summary>
    /// Extract the client's IP address, checking for X-Forwarded-For header (proxy-aware),
    /// with fallback to direct connection IP.
    /// </summary>
    /// <remarks>
    /// SECURITY: <c>X-Forwarded-For</c> is client-supplied and trivially spoofable. Only trust the logged
    /// <c>ClientIp</c> if a trusted reverse proxy sets this header and you have configured
    /// <c>ForwardedHeadersMiddleware</c> (which validates known proxies and populates
    /// <c>Connection.RemoteIpAddress</c>). Without that, treat this value as untrusted.
    /// </remarks>
    private static string? ExtractClientIp(HttpContext httpContext)
    {
        try
        {
            if (httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
            {
                var forwardedForValue = forwardedFor.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(forwardedForValue))
                {
                    var ips = forwardedForValue.Split(',');
                    if (ips.Length > 0)
                    {
                        var clientIp = ips[0].Trim();
                        if (!string.IsNullOrWhiteSpace(clientIp))
                        {
                            return clientIp;
                        }
                    }
                }
            }

            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrWhiteSpace(remoteIp))
            {
                return remoteIp;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract the User-Agent header from the request.</summary>
    private static string? ExtractUserAgent(HttpRequest request)
    {
        try
        {
            if (request.Headers.TryGetValue("User-Agent", out var userAgentValue))
            {
                var userAgent = userAgentValue.FirstOrDefault();
                return !string.IsNullOrWhiteSpace(userAgent) ? userAgent : null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOpenTelemetryRegistered(IServiceCollection services)
    {
        // OTel SDK renamed OpenTelemetrySdkHostedService to TelemetryHostedService in v1.15.0;
        // check both names to support older and newer package versions.
        return services.Any(sd =>
            sd.ServiceType == typeof(IHostedService) &&
            (sd.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.OpenTelemetrySdkHostedService"
             || sd.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.TelemetryHostedService"));
    }

    /// <summary>Parse OTEL_EXPORTER_OTLP_HEADERS environment variable (format: key1=value1,key2=value2)</summary>
    private static Dictionary<string, string> ParseOtlpHeaders(string? headerString)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(headerString)) return headers;

        foreach (var pair in headerString.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                headers[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return headers;
    }

    /// <summary>Parse OTEL_RESOURCE_ATTRIBUTES environment variable (format: key1=value1,key2=value2)</summary>
    private static Dictionary<string, object> ParseResourceAttributes(string? attributeString)
    {
        var attributes = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(attributeString)) return attributes;

        foreach (var pair in attributeString.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                attributes[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return attributes;
    }

    private static void ConfigureOutputSinks(LoggerConfiguration lc,
        IHostApplicationBuilder builder,
        string? logFilePath,
        StepUpLoggingOptions opts)
    {
        if (opts.EnableOtlpExporter)
        {
            lc.WriteTo.Async(a => a.OpenTelemetry(otlpOptions =>
            {
                var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
                var protocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");

                otlpOptions.Endpoint = endpoint;
                otlpOptions.Protocol = protocol == "http"
                    ? OtlpProtocol.HttpProtobuf
                    : OtlpProtocol.Grpc;

                var headers = ParseOtlpHeaders(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));
                foreach (var header in headers)
                {
                    otlpOptions.Headers[header.Key] = header.Value;
                }

                var attributes = ParseResourceAttributes(Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"));
                foreach (var attr in attributes)
                {
                    otlpOptions.ResourceAttributes[attr.Key] = attr.Value;
                }
            }));
        }

        if (opts.EnableConsoleLogging)
        {
            lc.WriteTo.Async(a => a.Console(new CompactJsonFormatter()));
        }

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var absolutePath = Path.IsPathRooted(logFilePath)
                ? logFilePath
                : Path.Combine(builder.Environment.ContentRootPath, logFilePath);

            lc.WriteTo.Async(a => a.File(
                formatter: new CompactJsonFormatter(),
                path: absolutePath!,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true));
        }
    }
}

internal sealed record CompiledRedactionPatterns(Regex[] Patterns)
{
    /// <summary>The sentinel returned in place of a value whose redaction failed, so a secret is never leaked.</summary>
    internal const string RedactionError = "[REDACTION-ERROR]";

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input) || Patterns.Length == 0) return input;
        foreach (var pattern in Patterns)
        {
            try
            {
                input = pattern.Replace(input, "[REDACTED]");
            }
            catch
            {
                // Fail closed: a pattern that throws (e.g. RegexMatchTimeoutException) must never
                // leak the original value. Continue with the remaining patterns on the sentinel.
                input = RedactionError;
            }
        }
        return input;
    }
}
