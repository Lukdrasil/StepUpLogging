using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Sinks.OpenTelemetry;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// OTLP export, configured from the <c>OTEL_EXPORTER_OTLP_*</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c> environment
/// variables only (ADR 0013, ADR 0036): the OpenTelemetry SDK exporters for traces and metrics, and the Serilog
/// OTLP sink for logs.
/// </summary>
internal static class OtlpExport
{
    /// <summary>
    /// Registers OTLP export for traces and metrics. An app that registered the OpenTelemetry SDK itself keeps its
    /// own exporter if it has one; otherwise the SDK is registered with <c>UseOtlpExporter</c>, as Aspire does.
    /// </summary>
    internal static void Register(IServiceCollection services)
    {
        if (IsOpenTelemetryRegistered(services))
        {
            AddOtlpExportersUnlessAppHasOne(services);
        }
        else
        {
            services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    /// <summary>Points the Serilog OTLP sink at the endpoint, protocol, timeout, headers and resource attributes from the environment.</summary>
    internal static void ConfigureSink(BatchedOpenTelemetrySinkOptions options)
    {
        options.Endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        options.Protocol = ResolveOtlpProtocol(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL"));
        options.HttpMessageHandler = new OtlpExportTimeoutHandler(
            ResolveOtlpTimeout(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TIMEOUT")));

        foreach (var (key, value) in ParseBaggageStylePairs(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS")))
        {
            options.Headers[key] = value;
        }

        foreach (var (key, value) in ParseBaggageStylePairs(Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES")))
        {
            options.ResourceAttributes[key] = value;
        }
    }

    /// <summary>
    /// Resolves the OTLP wire protocol from the <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> value. Both
    /// <c>"http"</c> and the spec-canonical <c>"http/protobuf"</c> map to
    /// <see cref="OtlpProtocol.HttpProtobuf"/> (case-insensitive); <c>"grpc"</c>, empty, null, or any
    /// unrecognized value fall back to <see cref="OtlpProtocol.Grpc"/>.
    /// </summary>
    internal static OtlpProtocol ResolveOtlpProtocol(string? protocol)
        => protocol?.Trim().ToLowerInvariant() switch
        {
            "http" or "http/protobuf" => OtlpProtocol.HttpProtobuf,
            _ => OtlpProtocol.Grpc,
        };

    /// <summary>
    /// Resolves the longest wait for one OTLP export from the <c>OTEL_EXPORTER_OTLP_TIMEOUT</c> value in
    /// milliseconds. A missing, unparseable or non-positive value falls back to the spec default of 10 s.
    /// </summary>
    internal static TimeSpan ResolveOtlpTimeout(string? milliseconds)
        => int.TryParse(milliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? TimeSpan.FromMilliseconds(value)
            : TimeSpan.FromSeconds(10);

    /// <summary>
    /// Splits a comma-separated, percent-encoded key=value list (the W3C Baggage-style format of
    /// <c>OTEL_EXPORTER_OTLP_HEADERS</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c>), percent-decoding both sides and skipping
    /// malformed pairs or empty keys.
    /// </summary>
    internal static IEnumerable<(string Key, string Value)> ParseBaggageStylePairs(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;

        foreach (var pair in input.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = Uri.UnescapeDataString(parts[0].Trim());
            var value = Uri.UnescapeDataString(parts[1].Trim());
            if (key.Length > 0) yield return (key, value);
        }
    }

    private static bool IsOpenTelemetryRegistered(IServiceCollection services)
    {
        // OTel SDK renamed OpenTelemetrySdkHostedService to TelemetryHostedService in v1.15.0 and moved it to the
        // Implementation namespace; match both names under any OpenTelemetry.Extensions.Hosting namespace.
        return services.Any(sd =>
            sd.ServiceType == typeof(IHostedService) &&
            sd.ImplementationType is { Namespace: { } ns, Name: "OpenTelemetrySdkHostedService" or "TelemetryHostedService" } &&
            ns.StartsWith("OpenTelemetry.Extensions.Hosting", StringComparison.Ordinal));
    }

    // The app registered the OpenTelemetry SDK first. Whether it exports over OTLP itself is decided when a provider
    // is built, against the final service collection, so its UseOtlpExporter or AddOtlpExporter is seen whether it
    // comes before or after AddStepUpLogging, and no second UseOtlpExporter registration makes OpenTelemetry throw.
    private static void AddOtlpExportersUnlessAppHasOne(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
        {
            if (HasOtlpExporterRegistration(services)) return;
            var options = new OtlpExporterOptions();
            var batch = options.BatchExportProcessorOptions;
            tracing.AddProcessor(new BatchActivityExportProcessor(
                new OtlpTraceExporter(options),
                batch.MaxQueueSize,
                batch.ScheduledDelayMilliseconds,
                batch.ExporterTimeoutMilliseconds,
                batch.MaxExportBatchSize));
        });
        services.ConfigureOpenTelemetryMeterProvider((_, metrics) =>
        {
            if (HasOtlpExporterRegistration(services)) return;
            var reader = new PeriodicExportingMetricReaderOptions();
            metrics.AddReader(new PeriodicExportingMetricReader(
                new OtlpMetricExporter(new OtlpExporterOptions()),
                reader.ExportIntervalMilliseconds ?? 60000,
                reader.ExportTimeoutMilliseconds ?? 30000));
        });
    }

    private static bool HasOtlpExporterRegistration(IServiceCollection services)
        => services.Any(sd =>
            sd.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>) ||
            sd.ServiceType.FullName == "OpenTelemetry.Exporter.UseOtlpExporterRegistration");
}
