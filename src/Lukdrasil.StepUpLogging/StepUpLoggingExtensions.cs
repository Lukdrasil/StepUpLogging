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
    /// <param name="configureOptions">Action to configure StepUpLoggingOptions</param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="enableConsoleLogging">Enable console logging</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions = null,
        string configSectionName = "SerilogStepUp",
        bool enableConsoleLogging = false,
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, configureOptions, null, configSectionName, enableConsoleLogging, logFilePath);
    }

    /// <summary>
    /// Adds StepUp logging with OpenTelemetry as the primary export mechanism.
    /// Configuration is loaded from appsettings.json section (default: "SerilogStepUp").
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configure">Optional additional Serilog configuration</param>
    /// <param name="configSectionName">Configuration section name (default: SerilogStepUp)</param>
    /// <param name="enableConsoleLogging">Enable console logging</param>
    /// <param name="logFilePath">Optional file path for additional file sink</param>
    public static IHostApplicationBuilder AddStepUpLogging(this IHostApplicationBuilder builder,
        Action<HostBuilderContext, IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName = "SerilogStepUp",
        bool enableConsoleLogging = false,
        string? logFilePath = null)
    {
        return AddStepUpLoggingInternal(builder, null, configure, configSectionName, enableConsoleLogging, logFilePath);
    }

    private static IHostApplicationBuilder AddStepUpLoggingInternal(
        IHostApplicationBuilder builder,
        Action<StepUpLoggingOptions>? configureOptions,
        Action<HostBuilderContext, IServiceProvider, LoggerConfiguration>? configure,
        string configSectionName,
        bool enableConsoleLogging,
        string? logFilePath)
    {
        var configSnapshot = new StepUpLoggingOptions();
        builder.Configuration.GetSection(configSectionName).Bind(configSnapshot);

        if (configSnapshot.EnableOtlpExporter && !IsOpenTelemetryRegistered(builder.Services))
        {
            // Align with Aspire defaults: ensure OTLP exporter is registered when OTLP logging is enabled
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Ensure default values are preserved when configuration section doesn't exist
        builder.Services.AddSingleton<IOptions<StepUpLoggingOptions>>(sp =>
        {
            var options = new StepUpLoggingOptions();
            builder.Configuration.GetSection(configSectionName).Bind(options);
            configureOptions?.Invoke(options);
            return Options.Create(options);
        });

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

        // Ensure Serilog DiagnosticContext is registered so Serilog.AspNetCore's RequestLoggingMiddleware can be activated.
        var diagTypeToResolve = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                              ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
        if (diagTypeToResolve != null)
        {
            builder.Services.AddSingleton(diagTypeToResolve, sp =>
            {
                try
                {
                    var logger = sp.GetService<Serilog.ILogger>();
                    var ctor = diagTypeToResolve.GetConstructors()
                        .OrderByDescending(c => c.GetParameters().Length)
                        .FirstOrDefault();
                    if (ctor != null)
                    {
                        var args = ctor.GetParameters().Select(p =>
                        {
                            if ((p.ParameterType == typeof(Serilog.ILogger) || p.ParameterType.FullName == "Serilog.Core.Logger") && logger != null)
                                return (object)logger;
                            if (p.HasDefaultValue) return p.DefaultValue;
                            return null;
                        }).ToArray();

                        if (args.Any(a => a == null))
                        {
                            return Activator.CreateInstance(diagTypeToResolve)!;
                        }

                        return ctor.Invoke(args);
                    }

                    return Activator.CreateInstance(diagTypeToResolve)!;
                }
                catch
                {
                    return null!;
                }
            });
        }

        builder.Services.AddSerilog((services, lc) =>
        {
            var stepUpController = services.GetRequiredService<StepUpLoggingController>();
            var opts = services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

            lc.ReadFrom.Configuration(builder.Configuration)
              .MinimumLevel.Verbose();

            ApplyCommonEnrichers(lc, builder, opts);

            // Bypass logger: exports at full verbosity independent of LevelSwitch.
            // Built directly here (not via DI) to avoid a circular deadlock:
            // AddSerilog registers Serilog.ILogger as a factory that depends on ILoggerFactory,
            // which in turn depends on this very callback — resolving it inside the callback deadlocks.
            var bypassLogger = CreateBypassLogger(builder, enableConsoleLogging, logFilePath, opts);
            try { stepUpController.SetSummaryLogger(bypassLogger); } catch { }

            // Step-up sink: gated by LevelSwitch, drops bypass-marked events to prevent duplication.
            var stepUpInnerCfg = new LoggerConfiguration().MinimumLevel.Verbose();
            ConfigureOutputSinks(stepUpInnerCfg, builder, enableConsoleLogging, logFilePath, opts);
            lc.WriteTo.Sink(new StepUpSink(stepUpInnerCfg.CreateLogger(), stepUpController.LevelSwitch));

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

            configure?.Invoke(new HostBuilderContext(new Dictionary<object, object>()), services, lc);
        }, writeToProviders: false);

        return builder;
    }

    private static LogEventLevel ParseLogEventLevel(string? value, LogEventLevel fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<LogEventLevel>(value, true, out var lvl) ? lvl : fallback;
    }

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
        bool enableConsoleLogging,
        string? logFilePath,
        StepUpLoggingOptions opts)
    {
        var cfg = new LoggerConfiguration().MinimumLevel.Verbose();
        ApplyCommonEnrichers(cfg, builder, opts);
        ConfigureOutputSinks(cfg, builder, enableConsoleLogging, logFilePath, opts);
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

        var sensitiveHeaders = new HashSet<string>(BuiltInSensitiveHeaders);
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
                await next();
                sw.Stop();

                var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath;

                if (exclude.Contains(path) || excludePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

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
                stepUpController.EmitRequestSummary(
                    httpContext.Request.Method,
                    path,
                    httpContext.Response?.StatusCode ?? 0,
                    sw.Elapsed.TotalMilliseconds,
                    Activity.Current?.TraceId.ToString(),
                    redactedQs,
                    routeParams,
                    userAgent,
                    clientIp,
                    jti);
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
                    if (method == "POST" || method == "PUT" || method == "PATCH")
                    {
                        try
                        {
                            httpContext.Request.EnableBuffering();
                            var contentLength = httpContext.Request.ContentLength ?? 0;
                            var maxBytes = Math.Min(opts.MaxBodyCaptureBytes, (int)contentLength);

                            if (maxBytes > 0)
                            {
                                using (opts.EnableActivityInstrumentation
                                    ? RequestLoggingActivitySource.StartActivity("CaptureRequestBody", ActivityKind.Internal)
                                    : null)
                                {
                                    var buffer = new char[maxBytes];
                                    using var sr = new StreamReader(httpContext.Request.Body, Encoding.UTF8, true, maxBytes, leaveOpen: true);
                                    var read = sr.Read(buffer, 0, maxBytes);
                                    httpContext.Request.Body.Position = 0;
                                    var body = new string(buffer, 0, read);
                                    diagnosticContext.Set("RequestBody", compiledPatterns.Redact(body));
                                    BodyCaptureCounter.Add(1);
                                }
                            }
                            else
                            {
                                diagnosticContext.Set("RequestBody", "[EMPTY]");
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
        bool enableConsoleLogging,
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

        if (opts.EnableConsoleLogging || enableConsoleLogging)
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
                retainedFileCountLimit: 30));
        }
    }
}

internal sealed record CompiledRedactionPatterns(Regex[] Patterns)
{
    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input) || Patterns.Length == 0) return input;
        try
        {
            foreach (var pattern in Patterns)
            {
                input = pattern.Replace(input, "[REDACTED]");
            }
        }
        catch
        {
            // fall back to original
        }
        return input;
    }
}
