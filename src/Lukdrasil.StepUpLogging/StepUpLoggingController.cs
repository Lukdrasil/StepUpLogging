using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
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
    private DateTime _lastTriggerTime = DateTime.MinValue;
    private DateTime _stepUpStartTime;
    private bool _disposed;

    private static readonly Meter Meter = new("StepUpLogging", "1.0.0");
    private static readonly Counter<long> TriggerCounter = Meter.CreateCounter<long>("stepup_trigger_total", "count", "Total number of step-up triggers");
    private static readonly Counter<long> SkippedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_skipped_total", "count", "Number of skipped triggers due to rate-limiting");
    private static readonly Histogram<double> StepUpDurationHistogram = Meter.CreateHistogram<double>("stepup_duration_seconds", "seconds", "Duration of step-up windows");
    private readonly UpDownCounter<int> _activeStepUpCounter = Meter.CreateUpDownCounter<int>("stepup_active", "state", "Whether step-up is active (1) or not (0)");

    public LoggingLevelSwitch LevelSwitch { get; }

    public StepUpLoggingController(StepUpLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _mode = options.Mode;
        _baseLevel = Parse(options.BaseLevel, LogEventLevel.Warning);
        _stepUpLevel = Parse(options.StepUpLevel, LogEventLevel.Information);
        _duration = TimeSpan.FromSeconds(options.DurationSeconds <= 0 ? 300 : options.DurationSeconds);
        _enableActivityInstrumentation = options.EnableActivityInstrumentation;

        // Initialize level based on mode
        var initialLevel = _mode switch
        {
            StepUpMode.AlwaysOn => _stepUpLevel,
            StepUpMode.Disabled => _baseLevel,
            _ => _baseLevel
        };
        LevelSwitch = new LoggingLevelSwitch(initialLevel);
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
                var now = DateTime.UtcNow;
                if (now - _lastTriggerTime < _minTriggerInterval)
                {
                    SkippedTriggerCounter.Add(1);
                    return;
                }

                _timer?.Change(_duration, Timeout.InfiniteTimeSpan);
                _lastTriggerTime = now;
                return;
            }

            // Transition to stepped-up state
            using (_enableActivityInstrumentation ? StepUpLoggingExtensions.ControllerActivitySource.StartActivity("TriggerStepUp", ActivityKind.Internal) : null)
            {
                LevelSwitch.MinimumLevel = _stepUpLevel;
                _lastTriggerTime = DateTime.UtcNow;
                _stepUpStartTime = _lastTriggerTime;

                TriggerCounter.Add(1);
                _activeStepUpCounter.Add(1);

                Log.Warning("Logging step up: increased minimum level to {Level} for {DurationSeconds} seconds", 
                    _stepUpLevel, (int)_duration.TotalSeconds);

                // Ensure timer is created and started
                if (_timer is null)
                {
                    _timer = new Timer(StepDownCallback, null, _duration, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _timer.Change(_duration, Timeout.InfiniteTimeSpan);
                }
            }
        }
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

            // Activity is only created if step down actually occurs (not when disposed)
            using (_enableActivityInstrumentation ? StepUpLoggingExtensions.ControllerActivitySource.StartActivity("PerformStepDown", ActivityKind.Internal) : null)
            {
                LevelSwitch.MinimumLevel = _baseLevel;

                var duration = (DateTime.UtcNow - _stepUpStartTime).TotalSeconds;
                StepUpDurationHistogram.Record(duration);
                _activeStepUpCounter.Add(-1);

                Log.Warning("Logging step down: restored minimum level to {Level}", _baseLevel);
            }
        }
    }

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
