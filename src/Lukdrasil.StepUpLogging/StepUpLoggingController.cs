using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Owns the step-up level: raises it on <see cref="Trigger"/>, lowers it after <c>DurationSeconds</c>, and in
/// Diagnostic mode holds <c>DiagnosticLevel</c> for <c>DiagnosticDurationMinutes</c>. Resolve it from DI to trigger a
/// step-up by hand or to read its state. Thread-safe.
/// </summary>
public sealed class StepUpLoggingController : IDisposable
{
    private static readonly TimeSpan MinTriggerInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly StepUpSettings _settings;
    private readonly TimeProvider _time;

    private ITimer? _timer;
    private long _lastTriggerTimestamp;
    private long _stepUpStartTimestamp;
    private long? _cooldownStartTimestamp;
    private long _generation;
    private bool _disposed;
    // Authoritative record of step-up state. The LevelSwitch cannot stand in for it: when
    // BaseLevel == StepUpLevel the switch reads the same level whether stepped up or not.
    private bool _isSteppedUp;
    private ITimer? _diagnosticTimer;
    private bool _diagnosticActive;
    private Serilog.ILogger? _summaryLogger;

    private static readonly Meter Meter = new("StepUpLogging", "1.0.0");
    private static readonly Counter<long> TriggerCounter = Meter.CreateCounter<long>("stepup_trigger_total", "count", "Total number of step-up triggers");
    private static readonly Counter<long> SkippedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_skipped_total", "count", "Number of skipped triggers due to rate-limiting");
    private static readonly Counter<long> CapReachedCounter = Meter.CreateCounter<long>("stepup_cap_reached_total", "count", "Number of forced step-downs after the continuous step-up cap was reached");
    private static readonly Counter<long> SuppressedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_suppressed_total", "count", "Number of triggers ignored while inside the post-cap cooldown window");
    private static readonly Histogram<double> StepUpDurationHistogram = Meter.CreateHistogram<double>("stepup_duration_seconds", "seconds", "Duration of step-up windows");
    private static readonly UpDownCounter<int> DiagnosticActiveCounter = Meter.CreateUpDownCounter<int>("stepup_diagnostic_active", "state", "Whether Diagnostic mode is active (1) or not (0)");
    private readonly UpDownCounter<int> _activeStepUpCounter = Meter.CreateUpDownCounter<int>("stepup_active", "state", "Whether step-up is active (1) or not (0)");

    /// <summary>Creates a controller from unresolved options; <paramref name="timeProvider"/> defaults to <see cref="TimeProvider.System"/>.</summary>
    internal StepUpLoggingController(StepUpLoggingOptions options, Serilog.ILogger? summaryLogger = null, TimeProvider? timeProvider = null)
        : this(new StepUpSettings(options), summaryLogger, timeProvider ?? TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates a controller. Ownership of <paramref name="summaryLogger"/>, the bypass logger for summaries and
    /// Warnings, transfers to the controller: <see cref="Dispose"/> disposes it. <paramref name="timeProvider"/> times
    /// the step-down, the rate limit, the cap, the cooldown and the Diagnostic window.
    /// </summary>
    internal StepUpLoggingController(StepUpSettings settings, Serilog.ILogger? summaryLogger, TimeProvider timeProvider)
    {
        _settings = settings;
        _summaryLogger = summaryLogger;
        _time = timeProvider;
        _lastTriggerTimestamp = timeProvider.GetTimestamp();
        LevelSwitch = new LoggingLevelSwitch(settings.InitialLevel);
        if (settings.Mode == StepUpMode.Diagnostic)
        {
            DiagnosticExpiresAt = StartDiagnostic();
        }
    }

    /// <summary>The minimum level the step-up gate exports at right now.</summary>
    public LogEventLevel CurrentLevel => LevelSwitch.MinimumLevel;

    /// <summary>
    /// Gets whether logging is currently stepped up; always <see langword="true"/> while Diagnostic mode is active.
    /// </summary>
    public bool IsSteppedUp => _settings.CanStepUp ? IsDiagnosticActive || _isSteppedUp : _settings.Mode == StepUpMode.AlwaysOn;

    internal LoggingLevelSwitch LevelSwitch { get; }

    /// <summary>The resolved <c>BaseLevel</c>, the floor the step-up raises from.</summary>
    internal LogEventLevel BaseLevel => _settings.BaseLevel;

    /// <summary>The resolved <c>StepUpLevel</c>, the level the switch is raised to on trigger.</summary>
    internal LogEventLevel StepUpLevel => _settings.StepUpLevel;

    /// <summary>The resolved <c>DiagnosticLevel</c>: the level the switch sits at while Diagnostic mode is active.</summary>
    internal LogEventLevel DiagnosticLevel => _settings.DiagnosticLevel;

    /// <summary>When Diagnostic mode ends, fixed at construction. Unset outside <see cref="StepUpMode.Diagnostic"/>.</summary>
    internal DateTimeOffset DiagnosticExpiresAt { get; }

    /// <summary>True from construction in Diagnostic mode until its timer fires or the controller is disposed.</summary>
    internal bool IsDiagnosticActive => Volatile.Read(ref _diagnosticActive);

    /// <summary>
    /// The bypass logger, which the controller's Warnings and <see cref="RequestSummaryWriter"/> write through. The
    /// <c>Log.Logger</c> fallback is reached only by a controller built without a bypass logger, or after
    /// <see cref="Dispose"/>: the pipeline attaches its bypass logger when the logger is built, before any request or
    /// trigger.
    /// </summary>
    internal Serilog.ILogger BypassLogger => _summaryLogger ?? Log.Logger;

    private DateTimeOffset StartDiagnostic()
    {
        lock (_gate)
        {
            Volatile.Write(ref _diagnosticActive, true);
            DiagnosticActiveCounter.Add(1);
            _diagnosticTimer = _time.CreateTimer(_ => EndDiagnostic(), null, _settings.DiagnosticDuration, Timeout.InfiniteTimeSpan);
            return _time.GetUtcNow() + _settings.DiagnosticDuration;
        }
    }

    /// <summary>
    /// Attaches the bypass logger once the pipeline has built it; resolving it through DI inside the
    /// <c>AddSerilog</c> callback would deadlock. Ownership transfers to this controller.
    /// </summary>
    internal void SetSummaryLogger(Serilog.ILogger? summaryLogger)
    {
        _summaryLogger = summaryLogger;
    }

    private void EndDiagnostic()
    {
        lock (_gate)
        {
            if (_disposed || !_diagnosticActive)
            {
                return;
            }

            Volatile.Write(ref _diagnosticActive, false);
            LevelSwitch.MinimumLevel = _settings.BaseLevel;
            DiagnosticActiveCounter.Add(-1);
            BypassLogger.Warning("StepUp Diagnostic mode ended, running as Auto at {BaseLevel}", _settings.BaseLevel);
        }
    }

    /// <summary>
    /// Raises the level to <c>StepUpLevel</c> and extends the step-down timer; a no-op in AlwaysOn and Disabled mode, during the post-cap cooldown, and while Diagnostic mode is active.
    /// </summary>
    public void Trigger()
    {
        if (!_settings.CanStepUp)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _diagnosticActive || InCooldown())
            {
                return;
            }

            // Reads the explicit flag rather than comparing levels: when BaseLevel == StepUpLevel, the switch already
            // sits at StepUpLevel before the first trigger, which a level comparison cannot tell from a step-up.
            if (_isSteppedUp)
            {
                Extend();
            }
            else
            {
                StepUp();
            }
        }
    }

    /// <summary>Whether the post-cap cooldown still suppresses triggers; clears an expired cooldown. Holds <see cref="_gate"/>.</summary>
    private bool InCooldown()
    {
        if (_cooldownStartTimestamp is not { } start)
        {
            return false;
        }

        if (_time.GetElapsedTime(start) < _settings.StepUpCooldown)
        {
            SuppressedTriggerCounter.Add(1);
            return true;
        }

        _cooldownStartTimestamp = null;
        return false;
    }

    /// <summary>
    /// Extends a running step-up, unless it has run past the continuous cap, which forces a step-down and opens the
    /// cooldown (ADR 0010), or the last extension is younger than the rate limit. Holds <see cref="_gate"/>.
    /// </summary>
    private void Extend()
    {
        var now = _time.GetTimestamp();
        if (_settings.MaxContinuousStepUp != TimeSpan.Zero
            && _time.GetElapsedTime(_stepUpStartTimestamp, now) >= _settings.MaxContinuousStepUp)
        {
            // Dispose the live timer so its superseded callback cannot fire a second step-down (ADR 0002).
            _timer?.Dispose();
            _timer = null;
            PerformStepDown(forcedByCap: true);
            CapReachedCounter.Add(1);
            _cooldownStartTimestamp = now;
            return;
        }

        if (_time.GetElapsedTime(_lastTriggerTimestamp, now) < MinTriggerInterval)
        {
            SkippedTriggerCounter.Add(1);
            return;
        }

        _lastTriggerTimestamp = now;
        ArmTimer();
    }

    private void StepUp()
    {
        using (_settings.ActivityInstrumentation ? StepUpLoggingActivitySources.Controller.StartActivity("TriggerStepUp", ActivityKind.Internal) : null)
        {
            LevelSwitch.MinimumLevel = _settings.StepUpLevel;
            _isSteppedUp = true;
            _lastTriggerTimestamp = _time.GetTimestamp();
            _stepUpStartTimestamp = _lastTriggerTimestamp;

            TriggerCounter.Add(1);
            _activeStepUpCounter.Add(1);

            BypassLogger.Warning("Logging step up: increased minimum level to {Level} for {DurationSeconds} seconds",
                _settings.StepUpLevel, (int)_settings.StepUpDuration.TotalSeconds);

            ArmTimer();
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
        _timer = _time.CreateTimer(StepDownCallback, _generation, _settings.StepUpDuration, Timeout.InfiniteTimeSpan);
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

            PerformStepDown(forcedByCap: false);
        }
    }

    /// <summary>
    /// Restore the base level and settle the step-up metrics. Must be called while holding <see cref="_gate"/>.
    /// A no-op when not currently stepped up, which makes the method idempotent: a forced-cap step-down followed
    /// by an already-queued stale timer callback (which can pass the generation check) must not double-decrement
    /// the active-state counter or record a bogus duration sample. <paramref name="forcedByCap"/> is <c>true</c>
    /// when the continuous step-up cap forced this step-down rather than the normal timer.
    /// </summary>
    private void PerformStepDown(bool forcedByCap)
    {
        // Guard on the authoritative flag, not the level: when BaseLevel == StepUpLevel the switch reads the
        // shared level whether stepped up or not, so a level comparison could never short-circuit the second call.
        if (!_isSteppedUp)
        {
            return;
        }

        // Activity is only created if step down actually occurs (not when disposed)
        using var activity = _settings.ActivityInstrumentation ? StepUpLoggingActivitySources.Controller.StartActivity("PerformStepDown", ActivityKind.Internal) : null;
        activity?.SetTag("stepup.forced_by_cap", forcedByCap);

        LevelSwitch.MinimumLevel = _settings.BaseLevel;
        _isSteppedUp = false;

        StepUpDurationHistogram.Record(_time.GetElapsedTime(_stepUpStartTimestamp).TotalSeconds);
        _activeStepUpCounter.Add(-1);

        BypassLogger.Warning("Logging step down: restored minimum level to {Level}", _settings.BaseLevel);
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

    /// <summary>Stops the timers and disposes the bypass logger this controller owns, flushing its buffers.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _diagnosticTimer?.Dispose();
            _diagnosticTimer = null;
            if (_diagnosticActive)
            {
                Volatile.Write(ref _diagnosticActive, false);
                DiagnosticActiveCounter.Add(-1);
            }

            // Dispose and null timer - prevents further scheduling
            _timer?.Dispose();
            _timer = null;

            // Dispose the bypass/summary logger this controller owns, flushing its async OTLP/File
            // buffers on shutdown. Serilog.ILogger is not itself IDisposable; the concrete Logger is.
            (_summaryLogger as IDisposable)?.Dispose();
            _summaryLogger = null;
        }
    }
}
