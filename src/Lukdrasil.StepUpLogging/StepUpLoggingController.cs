using System;
using System.Diagnostics.Metrics;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

public sealed class StepUpLoggingController : IDisposable
{
    private readonly Lock _gate = new();
    private readonly StepUpMode _mode;
    private readonly LogEventLevel _baseLevel;
    private readonly LogEventLevel _stepUpLevel;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _minTriggerInterval = TimeSpan.FromSeconds(5);

    private Timer? _timer;
    private DateTime _lastTriggerTime = DateTime.MinValue;
    private DateTime _stepUpStartTime;

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

        // Fast-path: if already stepped up and recently triggered, just extend timer
        if (LevelSwitch.MinimumLevel == _stepUpLevel)
        {
            var now = DateTime.UtcNow;
            if (now - _lastTriggerTime < _minTriggerInterval)
            {
                SkippedTriggerCounter.Add(1);
                return;
            }

            lock (_gate)
            {
                _timer?.Change(_duration, Timeout.InfiniteTimeSpan);
                _lastTriggerTime = now;
                return;
            }
        }

        lock (_gate)
        {
            LevelSwitch.MinimumLevel = _stepUpLevel;
            _lastTriggerTime = DateTime.UtcNow;
            _stepUpStartTime = _lastTriggerTime;

            TriggerCounter.Add(1);
            _activeStepUpCounter.Add(1);

            Log.Warning("Logging step up: increased minimum level to {Level} for {DurationSeconds} seconds", _stepUpLevel, (int)_duration.TotalSeconds);

            _timer?.Change(_duration, Timeout.InfiniteTimeSpan);
            _timer ??= new Timer(_ =>
            {
                lock (_gate)
                {
                    LevelSwitch.MinimumLevel = _baseLevel;

                    var duration = (DateTime.UtcNow - _stepUpStartTime).TotalSeconds;
                    StepUpDurationHistogram.Record(duration);
                    _activeStepUpCounter.Add(-1);

                    Log.Warning("Logging step down: restored minimum level to {Level}", _baseLevel);

                    _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }, null, _duration, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
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
