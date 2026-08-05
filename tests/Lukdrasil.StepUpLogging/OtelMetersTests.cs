using System.Collections.Generic;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Characterization test pinning the exact set of meter names registered by
/// <see cref="StepUpLoggingExtensions.AddStepUpLoggingMeters"/>. Nothing else in the suite
/// covers this method today; an accidental drop, duplicate, or typo of one of the five
/// existing names must fail this test loudly.
/// </summary>
public class OtelMetersTests
{
    /// <summary>
    /// Records the meter name of every exported metric. Using a real <see cref="MeterProviderBuilder"/>
    /// plus a real exporter (rather than reflecting into SDK internals) means this test observes the
    /// same "is this meter subscribed?" decision the OTel SDK itself makes: instruments created under a
    /// meter name registered via <c>AddMeter</c> get exported; instruments under any other meter name
    /// are silently dropped by the SDK and never reach the exporter.
    /// </summary>
    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        public HashSet<string> ObservedMeterNames { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                ObservedMeterNames.Add(metric.MeterName);
            }

            return ExportResult.Success;
        }
    }

    [Fact]
    public void AddStepUpLoggingMeters_RegistersExactlyTheFiveKnownMeterNames()
    {
        var exporter = new CapturingExporter();
        using var reader = new BaseExportingMetricReader(exporter);

        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddStepUpLoggingMeters()
            .AddReader(reader)
            .Build();

        // One probe meter per name we expect to be registered, plus decoys that must NOT be
        // registered (a plausible future addition and a typo of an existing name), to prove the
        // exact set — not just "at least these five".
        var candidateNames = new[]
        {
            "StepUpLogging",
            "StepUpLogging.Sink",
            "StepUpLogging.RequestLogging",
            "StepUpLogging.Buffer",
            "StepUpLogging.Immediate",
            "StepUpLogging.Audit",
            "StepUpLogging.Sinks",
        };

        foreach (var name in candidateNames)
        {
            using var meter = new Meter(name);
            var counter = meter.CreateCounter<int>("probe");
            counter.Add(1);
        }

        provider.ForceFlush();

        var expected = new HashSet<string>
        {
            "StepUpLogging",
            "StepUpLogging.Sink",
            "StepUpLogging.RequestLogging",
            "StepUpLogging.Buffer",
            "StepUpLogging.Immediate",
        };

        Assert.Equal(expected, exporter.ObservedMeterNames);
    }
}
