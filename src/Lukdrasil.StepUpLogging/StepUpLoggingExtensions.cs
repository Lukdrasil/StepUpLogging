using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Events;
using Serilog.Sinks.File;

namespace Lukdrasil.StepUpLogging;

public static class StepUpLoggingExtensions
{
    private static readonly Meter RequestMeter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> BodyCaptureCounter = RequestMeter.CreateCounter<long>("request_body_captured_total", "count", "Number of requests with captured body");
    private static readonly Counter<long> RedactionCounter = RequestMeter.CreateCounter<long>("request_redaction_applied_total", "count", "Number of requests with redaction applied");

    public static WebApplicationBuilder AddStepUpLogging(this WebApplicationBuilder builder,
        Action<HostBuilderContext, IServiceProvider, LoggerConfiguration>? configure = null,
        string configSectionName = "SerilogStepUp",
        bool enableConsoleLogging = false,
        string? logFilePath = null)
    {
        // Ensure default values are preserved when configuration section doesn't exist
        builder.Services.AddSingleton<IOptions<StepUpLoggingOptions>>(sp =>
        {
            var options = new StepUpLoggingOptions(); // Initialize with default values from properties
            builder.Configuration.GetSection(configSectionName).Bind(options); // Override with config if exists
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

        builder.Host.UseSerilog((ctx, services, lc) =>
        {
            var stepUpController = services.GetRequiredService<StepUpLoggingController>();
            var opts = services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

            lc.ReadFrom.Configuration(ctx.Configuration)
              .MinimumLevel.ControlledBy(stepUpController.LevelSwitch)
              .Enrich.FromLogContext()
              .Enrich.WithProperty("Application", ctx.HostingEnvironment.ApplicationName);

            if (opts.EnrichWithEnvironment)
            {
                lc.Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName);
            }

            if (!string.IsNullOrWhiteSpace(opts.ServiceVersion))
            {
                lc.Enrich.WithProperty("ServiceVersion", opts.ServiceVersion);
            }

            // Conditionally add Console sink
            if (enableConsoleLogging)
            {
                lc.WriteTo.Async(a => a.Console(new CompactJsonFormatter()));
            }

            // Conditionally add File sink
            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                var absolutePath = Path.IsPathRooted(logFilePath)
                    ? logFilePath
                    : Path.Combine(ctx.HostingEnvironment.ContentRootPath, logFilePath);
                
                lc.WriteTo.Async(a => a.File(
                    formatter: new CompactJsonFormatter(),
                    path: absolutePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30));
            }

            lc.WriteTo.Sink(new StepUpTriggerSink(stepUpController));

            configure?.Invoke(ctx, services, lc);
        }, writeToProviders: true);

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
