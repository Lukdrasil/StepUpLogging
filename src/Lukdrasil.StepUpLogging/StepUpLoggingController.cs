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

    private readonly TimeSpan _maxContinuous;
    private readonly TimeSpan _cooldown;
    private long _cooldownUntilTimestamp;

    private Timer? _timer;
    private readonly Func<long> _clock;
    private long _lastTriggerTimestamp;
    private long _stepUpStartTimestamp;
    private long _generation;
    private bool _disposed;

    private static readonly Meter Meter = new("StepUpLogging", "1.0.0");
    private static readonly Counter<long> TriggerCounter = Meter.CreateCounter<long>("stepup_trigger_total", "count", "Total number of step-up triggers");
    private static readonly Counter<long> SkippedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_skipped_total", "count", "Number of skipped triggers due to rate-limiting");
    private static readonly Counter<long> CapReachedCounter = Meter.CreateCounter<long>("stepup_cap_reached_total", "count", "Number of forced step-downs after the continuous step-up cap was reached");
    private static readonly Counter<long> SuppressedTriggerCounter = Meter.CreateCounter<long>("stepup_trigger_suppressed_total", "count", "Number of triggers ignored while inside the post-cap cooldown window");
    private static readonly Histogram<double> StepUpDurationHistogram = Meter.CreateHistogram<double>("stepup_duration_seconds", "seconds", "Duration of step-up windows");
    private readonly UpDownCounter<int> _activeStepUpCounter = Meter.CreateUpDownCounter<int>("stepup_active", "state", "Whether step-up is active (1) or not (0)");

    public LoggingLevelSwitch LevelSwitch { get; }

    /// <summary>The resolved <c>BaseLevel</c> — the floor the step-up raises from.</summary>
    internal LogEventLevel BaseLevel => _baseLevel;

    private readonly LogEventLevel _requestSummaryLevel;
    private Serilog.ILogger? _summaryLogger;

    public StepUpLoggingController(StepUpLoggingOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Creates a controller with an attached summary/bypass logger. Ownership of <paramref name="summaryLogger"/>
    /// transfers to the controller — <see cref="Dispose"/> disposes it. See <see cref="SetSummaryLogger"/>.
    /// </summary>
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
        // Single canonical default (180) matching StepUpLoggingOptions; startup validation rejects <= 0,
        // so this guard only defends direct construction in tests (ADR 0007).
        _duration = TimeSpan.FromSeconds(options.DurationSeconds <= 0 ? 180 : options.DurationSeconds);
        _maxContinuous = options.MaxContinuousStepUpSeconds > 0 ? TimeSpan.FromSeconds(options.MaxContinuousStepUpSeconds) : TimeSpan.Zero;
        _cooldown = _maxContinuous == TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(options.StepUpCooldownSeconds);
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
    /// <remarks>
    /// Ownership transfers to this controller: <see cref="Dispose"/> disposes the attached logger to flush its
    /// async buffers on shutdown. Pass a logger dedicated to this controller — not a shared logger you keep using
    /// elsewhere (e.g. <c>Log.Logger</c>), which would be disposed out from under you.
    /// </remarks>
    public void SetSummaryLogger(Serilog.ILogger? summaryLogger)
    {
        _summaryLogger = summaryLogger;
    }

    /// <summary>
    /// Emit a structured request summary using the configured summary logger (bypass) if available.
    /// </summary>
    /// <param name="method">HTTP method of the request.</param>
    /// <param name="path">Normalized request path.</param>
    /// <param name="statusCode">HTTP status code of the response.</param>
    /// <param name="elapsedMs">Request duration in milliseconds.</param>
    /// <param name="traceId">W3C trace identifier, when available.</param>
    /// <param name="queryString">Redacted query string, when present.</param>
    /// <param name="routeParameters">Redacted route parameters, when present.</param>
    /// <param name="userAgent">Redacted User-Agent header, when present.</param>
    /// <param name="clientIp">Client IP address (see <see cref="StepUpLoggingOptions.TrustForwardedHeaders"/>).</param>
    /// <param name="jti">Redacted token identifier claim, when present.</param>
    /// <param name="forwardedFor">Redacted raw <c>X-Forwarded-For</c> header, when present. Client-supplied and untrusted.</param>
    public void EmitRequestSummary(string method, string path, int statusCode, double elapsedMs, string? traceId = null, string? queryString = null, IReadOnlyDictionary<string, object?>? routeParameters = null, string? userAgent = null, string? clientIp = null, string? jti = null, string? forwardedFor = null)
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

            if (!string.IsNullOrEmpty(forwardedFor))
            {
                logger = logger.ForContext("ForwardedFor", forwardedFor);
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

            // Post-cap cooldown: while active, ignore triggers entirely; once expired, clear and proceed.
            if (_maxContinuous != TimeSpan.Zero && _cooldownUntilTimestamp != 0)
            {
                if (_clock() < _cooldownUntilTimestamp)
                {
                    SuppressedTriggerCounter.Add(1);
                    return;
                }

                _cooldownUntilTimestamp = 0;
            }

            // Fast-path: if already stepped up and recently triggered, just extend timer
            if (LevelSwitch.MinimumLevel == _stepUpLevel)
            {
                var now = _clock();

                // Hard cap: once step-up has been continuously active past the bound, force a step-down and
                // open the cooldown. Checked before the rate-limit/extend so a caller who keeps failing can
                // no longer hold the level indefinitely.
                if (_maxContinuous != TimeSpan.Zero
                    && Stopwatch.GetElapsedTime(_stepUpStartTimestamp, now) >= _maxContinuous)
                {
                    // Dispose the live timer so its superseded callback cannot fire a second step-down (ADR 0002).
                    _timer?.Dispose();
                    _timer = null;
                    PerformStepDown(forcedByCap: true);
                    CapReachedCounter.Add(1);
                    _cooldownUntilTimestamp = now + ToStopwatchTicks(_cooldown);
                    return;
                }

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

            PerformStepDown(forcedByCap: false);
        }
    }

    /// <summary>
    /// Restore the base level and settle the step-up metrics. Must be called while holding <see cref="_gate"/>.
    /// A no-op when the switch is not currently stepped up, which guards against a double step-down that would
    /// drive the active-state counter negative and record a bogus duration sample. <paramref name="forcedByCap"/>
    /// is <c>true</c> when the continuous step-up cap forced this step-down rather than the normal timer.
    /// </summary>
    private void PerformStepDown(bool forcedByCap)
    {
        if (LevelSwitch.MinimumLevel != _stepUpLevel)
        {
            return;
        }

        // Activity is only created if step down actually occurs (not when disposed)
        using var activity = _enableActivityInstrumentation ? StepUpLoggingExtensions.ControllerActivitySource.StartActivity("PerformStepDown", ActivityKind.Internal) : null;
        activity?.SetTag("stepup.forced_by_cap", forcedByCap);

        LevelSwitch.MinimumLevel = _baseLevel;

        var duration = Stopwatch.GetElapsedTime(_stepUpStartTimestamp, _clock()).TotalSeconds;
        StepUpDurationHistogram.Record(duration);
        _activeStepUpCounter.Add(-1);

        Log.Warning("Logging step down: restored minimum level to {Level}", _baseLevel);
    }

    private static long ToStopwatchTicks(TimeSpan span)
        => (long)(span.TotalSeconds * Stopwatch.Frequency);

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
    /// Test-only accessor reporting whether the post-cap cooldown window is currently active. Reads under the lock.
    /// </summary>
    internal bool IsInCooldown
    {
        get
        {
            lock (_gate)
            {
                return _cooldownUntilTimestamp != 0 && _clock() < _cooldownUntilTimestamp;
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

            // Dispose the bypass/summary logger this controller owns, flushing its async OTLP/File
            // buffers on shutdown. Serilog.ILogger is not itself IDisposable; the concrete Logger is.
            (_summaryLogger as IDisposable)?.Dispose();
            _summaryLogger = null;
        }
    }

    private static LogEventLevel Parse(string? value, LogEventLevel fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<LogEventLevel>(value, true, out var lvl) ? lvl : fallback;
    }
}
