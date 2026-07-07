using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Manages step-up logging level transitions and duration control.
/// Implements proper timer lifecycle management to prevent resource leaks.
/// Thread-safe design with minimal locking.
/// </summary>
public sealed class StepUpLoggingController : IDisposable
{
    private readonly object _gate = new();
    private readonly StepUpMode _mode;
    private readonly LogEventLevel _baseLevel;
    private readonly LogEventLevel _stepUpLevel;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _minTriggerInterval = TimeSpan.FromSeconds(5);
    private readonly bool _enableActivityInstrumentation;

    private Timer? _timer;
    private readonly Func<long> _clock;
    private long _lastTriggerTimestamp;
    private long _stepUpStartTimestamp;
    private long _generation;
    private bool _disposed;

    private static readonly Meter Meter = new("StepUpLogging", "1.0.0");
    private static readonly Counter<long> TriggerCounter = Meter.CreateCounter<long>("stepup_trigger_total", "count", "Total number of step-up triggers");
    private static readonly Counter<long> SkippedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_skipped_total", "count", "Number of skipped triggers due to rate-limiting");
    private static readonly Histogram<double> StepUpDurationHistogram = Meter.CreateHistogram<double>("stepup_duration_seconds", "seconds", "Duration of step-up windows");
    private readonly UpDownCounter<int> _activeStepUpCounter = Meter.CreateUpDownCounter<int>("stepup_active", "state", "Whether step-up is active (1) or not (0)");

    public LoggingLevelSwitch LevelSwitch { get; }

    private readonly LogEventLevel _requestSummaryLevel;
    private Serilog.ILogger? _summaryLogger;

    public StepUpLoggingController(StepUpLoggingOptions options)
        : this(options, null)
    {
    }

    public StepUpLoggingController(StepUpLoggingOptions options, Serilog.ILogger? summaryLogger)
        : this(options, summaryLogger, Stopwatch.GetTimestamp)
    {
    }

    /// <summary>
    /// Test-only constructor allowing a monotonic clock to be injected. The <paramref name="clock"/>
    /// must return timestamps in <see cref="Stopwatch"/> tick units (as produced by <see cref="Stopwatch.GetTimestamp"/>).
    /// </summary>
    internal StepUpLoggingController(StepUpLoggingOptions options, Serilog.ILogger? summaryLogger, Func<long> clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock;
        _lastTriggerTimestamp = clock();
        _mode = options.Mode;
        _baseLevel = Parse(options.BaseLevel, LogEventLevel.Warning);
        _stepUpLevel = Parse(options.StepUpLevel, LogEventLevel.Information);
        _duration = TimeSpan.FromSeconds(options.DurationSeconds <= 0 ? 300 : options.DurationSeconds);
        _enableActivityInstrumentation = options.EnableActivityInstrumentation;
        _summaryLogger = summaryLogger;
        _requestSummaryLevel = Parse(options.RequestSummaryLevel, LogEventLevel.Information);

        // Initialize level based on mode
        var initialLevel = _mode switch
        {
            StepUpMode.AlwaysOn => _stepUpLevel,
            StepUpMode.Disabled => _baseLevel,
            _ => _baseLevel
        };
        LevelSwitch = new LoggingLevelSwitch(initialLevel);
    }

    /// <summary>
    /// Attach or replace the summary/bypass logger. Useful for wiring the bypass logger after AddSerilog has
    /// created it to avoid circular DI during startup.
    /// </summary>
    public void SetSummaryLogger(Serilog.ILogger? summaryLogger)
    {
        _summaryLogger = summaryLogger;
    }

    /// <summary>
    /// Emit a structured request summary using the configured summary logger (bypass) if available.
    /// </summary>
    public void EmitRequestSummary(string method, string path, int statusCode, double elapsedMs, string? traceId = null, string? queryString = null, IReadOnlyDictionary<string, object?>? routeParameters = null, string? userAgent = null, string? clientIp = null, string? jti = null)
    {
        try
        {
            var lvl = _requestSummaryLevel;
            var logger = _summaryLogger is not null
                ? _summaryLogger.ForContext(LogProperties.IsRequestSummary, true)
                : Log.ForContext(LogProperties.IsRequestSummary, true);

            if (!string.IsNullOrEmpty(queryString))
            {
                logger = logger.ForContext("QueryString", queryString);
            }

            if (routeParameters != null && routeParameters.Count > 0)
            {
                logger = logger.ForContext("RouteParameters", routeParameters);
            }

            if (!string.IsNullOrEmpty(userAgent))
            {
                logger = logger.ForContext("UserAgent", userAgent);
            }

            if (!string.IsNullOrEmpty(clientIp))
            {
                logger = logger.ForContext("ClientIp", clientIp);
            }

            if (!string.IsNullOrEmpty(jti))
            {
                logger = logger.ForContext("Jti", jti);
            }

            logger.Write(lvl, "Request finished {Method} {Path} {StatusCode} {ElapsedMs}", method, path, statusCode, elapsedMs);
        }
        catch
        {
            // Swallow to avoid affecting request pipeline
        }
    }

    public bool IsSteppedUp => _mode switch
    {
        StepUpMode.AlwaysOn => true,
        StepUpMode.Disabled => false,
        _ => LevelSwitch.MinimumLevel == _stepUpLevel
    };

    public void Trigger()
    {
        // Ignore triggers in AlwaysOn or Disabled mode
        if (_mode == StepUpMode.AlwaysOn || _mode == StepUpMode.Disabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Fast-path: if already stepped up and recently triggered, just extend timer
            if (LevelSwitch.MinimumLevel == _stepUpLevel)
            {
                var now = _clock();
                if (Stopwatch.GetElapsedTime(_lastTriggerTimestamp, now) < _minTriggerInterval)
                {
                    SkippedTriggerCounter.Add(1);
                    return;
                }

                _lastTriggerTimestamp = now;
                ArmTimer();
                return;
            }

            // Transition to stepped-up state
            using (_enableActivityInstrumentation ? StepUpLoggingExtensions.ControllerActivitySource.StartActivity("TriggerStepUp", ActivityKind.Internal) : null)
            {
                LevelSwitch.MinimumLevel = _stepUpLevel;
                _lastTriggerTimestamp = _clock();
                _stepUpStartTimestamp = _lastTriggerTimestamp;

                TriggerCounter.Add(1);
                _activeStepUpCounter.Add(1);

                Log.Warning("Logging step up: increased minimum level to {Level} for {DurationSeconds} seconds",
                    _stepUpLevel, (int)_duration.TotalSeconds);

                ArmTimer();
            }
        }
    }

    /// <summary>
    /// Dispose the current step-down timer (if any) and arm a fresh one carrying the current generation
    /// as its callback state. Must be called while holding <see cref="_gate"/>. Each arm/extend advances
    /// <see cref="_generation"/> so that a step-down callback fired for a superseded window can be detected
    /// and ignored, avoiding a premature or double step-down.
    /// </summary>
    private void ArmTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = new Timer(StepDownCallback, _generation, _duration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Timer callback that transitions logging back to base level.
    /// Executed in thread pool context; uses lock to coordinate state.
    /// </summary>
    private void StepDownCallback(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Ignore a stale callback whose window was superseded by a concurrent trigger/extend.
            if (state is long capturedGeneration && capturedGeneration != _generation)
            {
                return;
            }

            // Only step down if currently stepped up; guards against a double step-down that would
            // drive the active-state counter negative and record a bogus duration sample.
            if (LevelSwitch.MinimumLevel != _stepUpLevel)
            {
                return;
            }

            // Activity is only created if step down actually occurs (not when disposed)
            using (_enableActivityInstrumentation ? StepUpLoggingExtensions.ControllerActivitySource.StartActivity("PerformStepDown", ActivityKind.Internal) : null)
            {
                LevelSwitch.MinimumLevel = _baseLevel;

                var duration = Stopwatch.GetElapsedTime(_stepUpStartTimestamp, _clock()).TotalSeconds;
                StepUpDurationHistogram.Record(duration);
                _activeStepUpCounter.Add(-1);

                Log.Warning("Logging step down: restored minimum level to {Level}", _baseLevel);
            }
        }
    }

    /// <summary>
    /// Test-only accessor for the current timer generation. Reads under the lock.
    /// </summary>
    internal long CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// Test-only deterministic invocation of the step-down callback with a captured generation,
    /// bypassing the real timer.
    /// </summary>
    internal void InvokeStepDownForTest(long capturedGeneration) => StepDownCallback(capturedGeneration);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Dispose and null timer - prevents further scheduling
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static LogEventLevel Parse(string? value, LogEventLevel fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<LogEventLevel>(value, true, out var lvl) ? lvl : fallback;
    }
}
