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

namespace Lukdrasil.StepUpLogging;

public static class StepUpLoggingExtensions
{
    /// <summary>
    /// ActivitySource for request logging operations (optional instrumentation).
    /// 
    /// Use this to enable tracing of request body capture and redaction operations in distributed tracing systems (Jaeger, Tempo).
    /// This is completely optional - if you don't register this ActivitySource in your OpenTelemetry config,
    /// the logging will work normally without distributed tracing.
    /// 
    /// Example registration:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => 
    ///         tracing.AddSource(StepUpLoggingExtensions.RequestLoggingActivitySourceName));
    /// </code>
    /// </summary>
    public static readonly ActivitySource RequestLoggingActivitySource = new("Lukdrasil.StepUpLogging.RequestLogging", "1.0.0");

    /// <summary>
    /// ActivitySource for buffer flush operations (optional instrumentation).
    /// 
    /// Use this to enable tracing of pre-error buffer flushing in distributed tracing systems.
    /// This is optional - buffer flushing works without this ActivitySource being registered.
    /// 
    /// Example registration:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => 
    ///         tracing.AddSource(StepUpLoggingExtensions.BufferActivitySourceName));
    /// </code>
    /// </summary>
    public static readonly ActivitySource BufferActivitySource = new("Lukdrasil.StepUpLogging.Buffer", "1.0.0");

    /// <summary>
    /// ActivitySource name for request logging (for explicit registration).
    /// </summary>
    public const string RequestLoggingActivitySourceName = "Lukdrasil.StepUpLogging.RequestLogging";

    /// <summary>
    /// ActivitySource name for buffer operations (for explicit registration).
    /// </summary>
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
            var options = new StepUpLoggingOptions(); // Initialize with default values from properties
            builder.Configuration.GetSection(configSectionName).Bind(options); // Override with config if exists
            configureOptions?.Invoke(options); // Allow programmatic override
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

        builder.Services.AddSerilog((services, lc) =>
{
    var stepUpController = services.GetRequiredService<StepUpLoggingController>();
    var opts = services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

    lc.ReadFrom.Configuration(builder.Configuration)
                // Root stays verbose; we gate actual outputs via sub-loggers so that buffering can see all events
                .MinimumLevel.Verbose()
  .Enrich.FromLogContext()
  .Enrich.WithOpenTelemetryTraceId()
  .Enrich.WithOpenTelemetrySpanId()
  .Enrich.With<ActivityContextEnricher>()
  .Enrich.WithProperty("Application", builder.Environment.ApplicationName);

    // Add exception details enrichment when enabled
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

    if (!string.IsNullOrWhiteSpace(opts.ServiceVersion))
    {
        lc.Enrich.WithProperty("ServiceVersion", opts.ServiceVersion);
    }

    if (!string.IsNullOrWhiteSpace(opts.ServiceInstanceId))
    {
        lc.Enrich.WithProperty("ServiceInstanceId", opts.ServiceInstanceId);
    }

    // Build a sub-logger for main outputs gated by the step-up level switch
    lc.WriteTo.Logger(l =>
    {
        l.MinimumLevel.ControlledBy(stepUpController.LevelSwitch);
        ConfigureStepUpSinks(l, builder, enableConsoleLogging, logFilePath, opts);
    });

    // Buffered pre-error branch (always receives all events); flushes to bypass logger
    if (opts.EnablePreErrorBuffering)
    {
        var bypassLoggerConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose(); // Always write buffered events regardless of level
        ConfigureStepUpSinks(bypassLoggerConfig, builder, enableConsoleLogging, logFilePath, opts);
        var bypassLogger = bypassLoggerConfig.CreateLogger();

        lc.WriteTo.Logger(l =>
        {
            l.MinimumLevel.Verbose();
            l.WriteTo.Sink(new PreErrorBufferSink(bypassLogger, opts.PreErrorBufferSize, opts.PreErrorMaxContexts));
        });
    }

    // Trigger sink observes error-level events (after enrichment) and triggers step-up
    lc.WriteTo.Sink(new StepUpTriggerSink(stepUpController));

    configure?.Invoke(new HostBuilderContext(new Dictionary<object, object>()), services, lc);
}, writeToProviders: false);

        return builder;
    }

    public static MeterProviderBuilder AddStepUpLoggingMeters(this MeterProviderBuilder builder)
    {
        return builder
            .AddMeter("StepUpLogging")
            .AddMeter("StepUpLogging.Sink")
            .AddMeter("StepUpLogging.RequestLogging")
            .AddMeter("StepUpLogging.Buffer");
    }

    /// <summary>
    /// Adds Serilog request logging middleware with enriched context (route parameters, headers, body capture).
    /// Integrates with StepUp logging to capture detailed request information when logging is stepped-up (on errors).
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for method chaining</returns>
    /// <remarks>
    /// This middleware logs the following request context:
    /// - Request path and query string (with sensitive data redaction)
    /// - Route parameters (e.g., {id}, {role})
    /// - HTTP headers (with automatic redaction of Authorization, Cookie, X-API-Key, etc.)
    /// - Request body (when CaptureRequestBody is enabled and logging is stepped-up)
    /// 
    /// Additional header names can be marked as sensitive via StepUpLoggingOptions.AdditionalSensitiveHeaders
    /// for automatic redaction in the logs.
    /// </remarks>
    public static WebApplication UseStepUpRequestLogging(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
        var stepUpController = app.Services.GetRequiredService<StepUpLoggingController>();
        var compiledPatterns = app.Services.GetRequiredService<CompiledRedactionPatterns>();
        
        // Cache exclude paths computation (done once, not per request)
        var exclude = new HashSet<string>(opts.ExcludePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var excludePrefixes = (opts.ExcludePaths ?? Array.Empty<string>())
            .Where(p => p.EndsWith("*", StringComparison.Ordinal))
            .Select(p => p.TrimEnd('*').TrimEnd('/'))
            .ToArray();
        
        // Build combined sensitive headers set (built-in + configured)
        var sensitiveHeaders = new HashSet<string>(BuiltInSensitiveHeaders);
        if (opts.AdditionalSensitiveHeaders?.Length > 0)
        {
            foreach (var header in opts.AdditionalSensitiveHeaders)
            {
                sensitiveHeaders.Add(header);
            }
        }

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                // Use ActivitySource for body capture and redaction operations when appropriate
                using var activity = RequestLoggingActivitySource.StartActivity("LogRequest", ActivityKind.Server);
                activity?.SetTag("http.method", httpContext.Request.Method);
                activity?.SetTag("http.target", httpContext.Request.Path.Value);

                var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath; // normalize trailing slash
                diagnosticContext.Set("RequestPath", path);

                var qs = httpContext.Request.QueryString.HasValue ? httpContext.Request.QueryString.Value! : string.Empty;
                var redactedQs = compiledPatterns.Redact(qs);
                if (!string.Equals(redactedQs, qs, StringComparison.Ordinal))
                {
                    RedactionCounter.Add(1);
                    activity?.SetTag("security.redaction_applied", true);
                }
                diagnosticContext.Set("QueryString", redactedQs);

                // Log route parameters
                if (httpContext.Request.RouteValues?.Count > 0)
                {
                    var routeParams = new Dictionary<string, object?>();
                    foreach (var kvp in httpContext.Request.RouteValues)
                    {
                        // Redact sensitive route parameter values
                        var value = kvp.Value?.ToString() ?? string.Empty;
                        var redactedValue = compiledPatterns.Redact(value);
                        routeParams[kvp.Key] = redactedValue;
                        
                        if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
                        {
                            RedactionCounter.Add(1);
                        }
                    }
                    diagnosticContext.Set("RouteParameters", routeParams);
                }

                // Log headers (with redaction of sensitive headers)
                var headers = new Dictionary<string, object?>();

                foreach (var header in httpContext.Request.Headers)
                {
                    if (sensitiveHeaders.Contains(header.Key))
                    {
                        headers[header.Key] = "[REDACTED]";
                    }
                    else
                    {
                        var value = string.Join(", ", header.Value.Where(v => v != null) ?? Array.Empty<string>());
                        var redactedValue = compiledPatterns.Redact(value);
                        headers[header.Key] = redactedValue;
                        
                        if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
                        {
                            RedactionCounter.Add(1);
                        }
                    }
                }
                diagnosticContext.Set("Headers", headers);

                if (opts.CaptureRequestBody && stepUpController.IsSteppedUp)
                {
                    var method = httpContext.Request.Method;
                    if (method == "POST" || method == "PUT" || method == "PATCH")
                    {
                        try
                        {
                            // Create activity span for body capture operation
                            using (RequestLoggingActivitySource.StartActivity("CaptureRequestBody", ActivityKind.Internal))
                            {
                                httpContext.Request.EnableBuffering();
                                var contentLength = httpContext.Request.ContentLength ?? 0;
                                var maxBytes = Math.Min(opts.MaxBodyCaptureBytes, (int)contentLength);

                                if (maxBytes > 0)
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

    private static bool IsOpenTelemetryRegistered(IServiceCollection services)
    {
        return services.Any(sd =>
            sd.ServiceType == typeof(IHostedService) &&
            sd.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.OpenTelemetrySdkHostedService");
    }

    /// <summary>
    /// Parse OTEL_EXPORTER_OTLP_HEADERS environment variable (format: key1=value1,key2=value2)
    /// </summary>
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

    /// <summary>
    /// Parse OTEL_RESOURCE_ATTRIBUTES environment variable (format: key1=value1,key2=value2)
    /// </summary>
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

    private static void ConfigureStepUpSinks(LoggerConfiguration lc,
        IHostApplicationBuilder builder,
        bool enableConsoleLogging,
        string? logFilePath,
        StepUpLoggingOptions opts)
    {
        // Primary sink: OpenTelemetry OTLP exporter (production-ready)
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

        // Optional: Console sink (for dev scenarios or direct console log collection)
        // Priority: opts.EnableConsoleLogging (from options) > enableConsoleLogging (from method parameter)
        if (opts.EnableConsoleLogging || enableConsoleLogging)
        {
            lc.WriteTo.Async(a => a.Console(new CompactJsonFormatter()));
        }

        // Conditionally add File sink
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

