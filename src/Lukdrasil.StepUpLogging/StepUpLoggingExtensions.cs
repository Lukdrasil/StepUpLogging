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
    private static readonly Meter RequestMeter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> BodyCaptureCounter = RequestMeter.CreateCounter<long>("request_body_captured_total", "count", "Number of requests with captured body");
    private static readonly Counter<long> RedactionCounter = RequestMeter.CreateCounter<long>("request_redaction_applied_total", "count", "Number of requests with redaction applied");

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
              .MinimumLevel.ControlledBy(stepUpController.LevelSwitch)
              .Enrich.FromLogContext()
              .Enrich.WithOpenTelemetryTraceId()
              .Enrich.WithOpenTelemetrySpanId()
              .Enrich.With<ActivityContextEnricher>()
              .Enrich.WithProperty("Application", builder.Environment.ApplicationName);

            if (opts.EnrichWithEnvironment)
            {
                lc.Enrich.WithProperty("Environment", builder.Environment.EnvironmentName);
            }

            if (!string.IsNullOrWhiteSpace(opts.ServiceVersion))
            {
                lc.Enrich.WithProperty("ServiceVersion", opts.ServiceVersion);
            }

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

                    // Apply headers from OTEL_EXPORTER_OTLP_HEADERS environment variable
                    var headers = ParseOtlpHeaders(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));
                    foreach (var header in headers)
                    {
                        otlpOptions.Headers[header.Key] = header.Value;
                    }

                    // Apply resource attributes from OTEL_RESOURCE_ATTRIBUTES environment variable
                    var attributes = ParseResourceAttributes(Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"));
                    foreach (var attr in attributes)
                    {
                        otlpOptions.ResourceAttributes[attr.Key] = attr.Value;
                    }
                }));
            }

            // Optional: Console sink (for dev scenarios or direct console log collection)
            if (enableConsoleLogging)
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
                    path: absolutePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30));
            }

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
            .AddMeter("StepUpLogging.RequestLogging");
    }

    public static WebApplication UseStepUpRequestLogging(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
        var stepUpController = app.Services.GetRequiredService<StepUpLoggingController>();
        var compiledPatterns = app.Services.GetRequiredService<CompiledRedactionPatterns>();
        var exclude = new HashSet<string>(opts.ExcludePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var excludePrefixes = (opts.ExcludePaths ?? Array.Empty<string>())
            .Where(p => p.EndsWith("*", StringComparison.Ordinal))
            .Select(p => p.TrimEnd('*').TrimEnd('/'))
            .ToArray();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                var rawPath = httpContext.Request.Path.Value ?? string.Empty;
                var path = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath; // normalize trailing slash
                diagnosticContext.Set("RequestPath", path);

                var qs = httpContext.Request.QueryString.HasValue ? httpContext.Request.QueryString.Value! : string.Empty;
                var redactedQs = compiledPatterns.Redact(qs);
                if (!string.Equals(redactedQs, qs, StringComparison.Ordinal))
                {
                    RedactionCounter.Add(1);
                }
                diagnosticContext.Set("QueryString", redactedQs);

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
                                var buffer = new char[maxBytes];
                                using var sr = new StreamReader(httpContext.Request.Body, Encoding.UTF8, true, maxBytes, leaveOpen: true);
                                var read = sr.Read(buffer, 0, maxBytes);
                                httpContext.Request.Body.Position = 0;
                                var body = new string(buffer, 0, read);
                                diagnosticContext.Set("RequestBody", compiledPatterns.Redact(body));
                                BodyCaptureCounter.Add(1);
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
}

internal sealed class CompiledRedactionPatterns
{
    public Regex[] Patterns { get; }

    public CompiledRedactionPatterns(Regex[] patterns)
    {
        Patterns = patterns;
    }

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

