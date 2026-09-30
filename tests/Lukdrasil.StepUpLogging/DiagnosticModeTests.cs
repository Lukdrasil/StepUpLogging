using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class DiagnosticModeTests
{
    private const string StartTemplate = "StepUp Diagnostic mode active at {DiagnosticLevel} for {DurationMinutes} min until {ExpiresAt}";
    private const string EndTemplate = "StepUp Diagnostic mode ended, running as Auto at {BaseLevel}";
    private const string DiagnosticLevelWarningText = "Diagnostic mode cannot increase verbosity";

    [Fact]
    public void Diagnostic_SwitchSitsAtDiagnosticLevelUntilExactlyTheDuration_ThenRunsAsAutoAtBaseLevel()
    {
        using var run = DiagnosticHost.Start(("DiagnosticDurationMinutes", "5"));

        Assert.Equal(LogEventLevel.Debug, run.Controller.LevelSwitch.MinimumLevel);
        Assert.True(run.Controller.IsSteppedUp);

        run.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Equal(LogEventLevel.Debug, run.Controller.LevelSwitch.MinimumLevel);
        Assert.True(run.Controller.IsSteppedUp);

        run.Time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(LogEventLevel.Warning, run.Controller.LevelSwitch.MinimumLevel);
        Assert.False(run.Controller.IsSteppedUp);
    }

    [Fact]
    public void Diagnostic_WithoutATimeProviderInDI_StartsAtDiagnosticLevel()
    {
        using var run = DiagnosticHost.Start([], registerFakeTime: false);

        Assert.Equal(LogEventLevel.Debug, run.Controller.LevelSwitch.MinimumLevel);
        Assert.True(run.Controller.IsSteppedUp);
    }

    [Fact]
    public void Diagnostic_TriggerDuringDiagnosticChangesNothing_AndATriggerAfterExpiryStepsUpNormally()
    {
        using var run = DiagnosticHost.Start(("DiagnosticDurationMinutes", "5"));
        using var stepUpActive = new NetGauge("stepup_active");

        run.Controller.Trigger();
        Assert.Equal(LogEventLevel.Debug, run.Controller.LevelSwitch.MinimumLevel);
        Assert.True(run.Controller.IsSteppedUp);
        Assert.Equal(0, stepUpActive.Value);
        Assert.Equal(0, run.Count("Logging step up"));

        run.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(LogEventLevel.Warning, run.Controller.LevelSwitch.MinimumLevel);
        Assert.False(run.Controller.IsSteppedUp);

        run.Controller.Trigger();
        Assert.Equal(LogEventLevel.Information, run.Controller.LevelSwitch.MinimumLevel);
        Assert.True(run.Controller.IsSteppedUp);
        Assert.Equal(1, stepUpActive.Value);
        Assert.Equal(1, run.Count("Logging step up"));
    }

    [Fact]
    public void Diagnostic_StartAndEndWarnings_MatchTheFixedTemplates_AndReachTheCaptureSinkAtBaseLevelError()
    {
        using var run = DiagnosticHost.Start(("BaseLevel", "Error"), ("DiagnosticDurationMinutes", "30"));
        var expiresAt = run.StartedAt + TimeSpan.FromMinutes(30);

        var start = Assert.Single(run.WithTemplate(StartTemplate));
        Assert.Equal(LogEventLevel.Warning, start.Level);
        Assert.Equal("Debug", Scalar(start, "DiagnosticLevel")?.ToString());
        Assert.Equal(30, Convert.ToInt32(Scalar(start, "DurationMinutes")));
        Assert.Equal(expiresAt, Assert.IsType<DateTimeOffset>(Scalar(start, "ExpiresAt")));
        Assert.Empty(run.WithTemplate(EndTemplate));

        run.Time.Advance(TimeSpan.FromMinutes(30));

        var end = Assert.Single(run.WithTemplate(EndTemplate));
        Assert.Equal(LogEventLevel.Warning, end.Level);
        Assert.Equal("Error", Scalar(end, "BaseLevel")?.ToString());
        Assert.Single(run.WithTemplate(StartTemplate));
    }

    [Fact]
    public void Diagnostic_Expiry_EmitsOnlyTheEndWarning_NoStepDownWarningAndNoControllerActivity()
    {
        using var run = DiagnosticHost.Start(("DiagnosticDurationMinutes", "5"));
        var started = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StepUpLoggingExtensions.ControllerActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => started++,
        };
        ActivitySource.AddActivityListener(activities);

        run.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Single(run.WithTemplate(EndTemplate));
        Assert.Equal(0, run.Count("Logging step down"));
        Assert.Equal(0, started);
    }

    [Fact]
    public void Diagnostic_ActiveGauge_ReadsOneWhileActive_ZeroAfterExpiry_AndStepUpActiveStaysZero()
    {
        using var diagnosticActive = new NetGauge("stepup_diagnostic_active");
        using var stepUpActive = new NetGauge("stepup_active");
        using var run = DiagnosticHost.Start(("DiagnosticDurationMinutes", "5"));

        Assert.Equal(1, diagnosticActive.Value);
        Assert.Equal(0, stepUpActive.Value);

        run.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, diagnosticActive.Value);
        Assert.Equal(0, stepUpActive.Value);
    }

    [Fact]
    public void Diagnostic_DisposeBeforeExpiry_DropsTheGaugeToZero_AndNoEndWarningIsEverWritten()
    {
        using var diagnosticActive = new NetGauge("stepup_diagnostic_active");
        using var run = DiagnosticHost.Start(("DiagnosticDurationMinutes", "5"));
        Assert.Equal(1, diagnosticActive.Value);

        run.Host.Dispose();
        Assert.Equal(0, diagnosticActive.Value);
        Assert.Empty(run.WithTemplate(EndTemplate));

        run.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, diagnosticActive.Value);
        Assert.Empty(run.WithTemplate(EndTemplate));
    }

    [Fact]
    public void Diagnostic_DiagnosticLevelNotMoreVerboseThanBaseLevel_WritesTheWiringWarning()
    {
        using var run = DiagnosticHost.Start(("BaseLevel", "Information"), ("StepUpLevel", "Debug"), ("DiagnosticLevel", "Information"));

        var warning = Assert.Single(run.Events(), e => e.RenderMessage().Contains(DiagnosticLevelWarningText, StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Equal(
            "DiagnosticLevel Information is not more verbose than BaseLevel Information; Diagnostic mode cannot increase verbosity.",
            warning.RenderMessage());
        Assert.Equal(0, run.Count("step-up cannot increase verbosity"));
    }

    [Fact]
    public void Diagnostic_DiagnosticLevelMoreVerboseThanBaseLevel_WritesNoDiagnosticLevelWarning()
    {
        using var run = DiagnosticHost.Start(("BaseLevel", "Information"), ("StepUpLevel", "Debug"), ("DiagnosticLevel", "Verbose"));

        Assert.Equal(1, run.Count("StepUp Diagnostic mode active"));
        Assert.Equal(0, run.Count(DiagnosticLevelWarningText));
    }

    [Fact]
    public void Auto_DiagnosticLevelNotMoreVerboseThanBaseLevel_WritesNoDiagnosticLevelWarning()
    {
        using var run = DiagnosticHost.Start(("Mode", "Auto"), ("DiagnosticLevel", "Error"));

        Assert.Equal(0, run.Count(DiagnosticLevelWarningText));
        Assert.Equal(0, run.Count("StepUp Diagnostic mode active"));
    }

    private static object? Scalar(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;

    internal sealed class NetGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _value;

        public NetGauge(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "StepUpLogging" && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => _value += measurement);
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => _value += measurement);
            _listener.Start();
        }

        public long Value => _value;

        public void Dispose() => _listener.Dispose();
    }

    internal sealed class DiagnosticHost : IDisposable
    {
        private readonly string _captureKey;

        private DiagnosticHost(IHost host, FakeTimeProvider time, string captureKey)
        {
            Host = host;
            Time = time;
            StartedAt = time.GetUtcNow();
            _captureKey = captureKey;
            Logger = host.Services.GetRequiredService<Serilog.ILogger>();
            Controller = host.Services.GetRequiredService<StepUpLoggingController>();
        }

        public IHost Host { get; }

        public FakeTimeProvider Time { get; }

        public DateTimeOffset StartedAt { get; }

        public Serilog.ILogger Logger { get; }

        public StepUpLoggingController Controller { get; }

        public static DiagnosticHost Start(params (string Key, string Value)[] overrides)
            => Start(overrides, registerFakeTime: true);

        public static DiagnosticHost Start((string Key, string Value)[] overrides, bool registerFakeTime)
            => Start(overrides, registerFakeTime, builder => builder.AddStepUpLogging());

        public static DiagnosticHost Start(
            (string Key, string Value)[] overrides,
            bool registerFakeTime,
            Action<HostApplicationBuilder> addStepUpLogging)
        {
            var captureKey = Guid.NewGuid().ToString("N");
            var settings = new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:Mode"] = "Diagnostic",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:DurationSeconds"] = "300",
                ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
                ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
                ["Serilog:WriteTo:0:Args:key"] = captureKey,
            };
            foreach (var (key, value) in overrides)
            {
                settings["SerilogStepUp:" + key] = value;
            }

            var time = new FakeTimeProvider();
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            if (registerFakeTime)
            {
                builder.Services.AddSingleton<TimeProvider>(time);
            }
            addStepUpLogging(builder);

            return new DiagnosticHost(builder.Build(), time, captureKey);
        }

        public LogEvent[] Events() => KeyedCaptureSink.Events(_captureKey);

        public int Count(string token) => KeyedCaptureSink.Count(_captureKey, token);

        public LogEvent[] WithTemplate(string template)
            => Events().Where(e => e.MessageTemplate.Text == template).ToArray();

        public void Dispose()
        {
            Host.Dispose();
            KeyedCaptureSink.Forget(_captureKey);
        }
    }
}
