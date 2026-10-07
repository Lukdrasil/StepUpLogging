using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Reports the audit records a full spool drops, at Critical but at a bounded rate: the first drop
/// of a window is logged by name, and the drops after it are counted and reported as one summary —
/// when the window has ended and the next drop arrives, when a record is stored again, or when the
/// log is disposed — so no loss goes unreported and none costs a log line each (ADR 0020 D6,
/// issue #69).
/// </summary>
/// <remarks>The window is <see cref="EncryptedSpoolOptions.SpoolFullRecheckInterval"/> long, the pace at which a full spool is looked at again. Thread-safe.</remarks>
internal sealed class DroppedRecordLog(EncryptedSpoolOptions options, TimeProvider timeProvider, ILogger logger) : IDisposable
{
    private readonly object _gate = new();
    private Window _window;

    /// <summary>Reports a record that was dropped because the spool, at <paramref name="usage"/>, is full.</summary>
    public void Dropped(Guid eventId, SpoolUsage usage)
    {
        var (opensWindow, ended) = Admit(eventId);
        Report(ended);

        if (opensWindow)
        {
            // Critical: this is deliberate, bounded, visible loss of an audit record, and the only
            // trace of it left is here and on the counter (ADR 0020 D6).
            logger.LogCritical(
                "Audit record {EventId} was dropped: the spool at {SpoolDirectory} is full with {SpooledRecords} of {SpoolMaxEntries} records ({SpooledBytes} of {SpoolMaxBytes} bytes). The record is lost and no retry will bring it back — the spool drains only as fast as the audit endpoint accepts it. Further drops within {WindowSeconds} s are reported together.",
                eventId, options.SpoolDirectory, usage.Records, options.SpoolMaxEntries, usage.Bytes, options.SpoolMaxBytes, options.SpoolFullRecheckInterval.TotalSeconds);
        }
    }

    /// <summary>Reports the drops held back so far, now that a record was stored: the spool has room again.</summary>
    public void Stored() => Report(TakeHeld());

    /// <summary>Reports the drops held back so far, so a stopping host does not leave them unreported.</summary>
    public void Dispose() => Report(TakeHeld());

    private (bool OpensWindow, Window Ended) Admit(Guid eventId)
    {
        lock (_gate)
        {
            var now = timeProvider.GetTimestamp();
            if (_window.IsOpen && timeProvider.GetElapsedTime(_window.Started, now) < options.SpoolFullRecheckInterval)
            {
                _window = _window.Hold(eventId);
                return (false, default);
            }

            var ended = _window;
            _window = new Window { IsOpen = true, Started = now };
            return (true, ended);
        }
    }

    private Window TakeHeld()
    {
        lock (_gate)
        {
            var held = _window;
            _window = _window.WithoutHeld();
            return held;
        }
    }

    private void Report(Window window)
    {
        if (window.Held == 0)
        {
            return;
        }

        logger.LogCritical(
            "{DroppedRecords} more audit records were dropped because the spool at {SpoolDirectory} was full, the first of them {FirstEventId} and the last {LastEventId}. These records are lost and no retry will bring them back.",
            window.Held, options.SpoolDirectory, window.FirstHeld, window.LastHeld);
    }

    /// <summary>A window of drops: when it opened, and how many drops it held back from the log, first and last.</summary>
    private readonly record struct Window
    {
        public bool IsOpen { get; init; }

        public long Started { get; init; }

        public int Held { get; init; }

        public Guid FirstHeld { get; init; }

        public Guid LastHeld { get; init; }

        public Window Hold(Guid eventId) =>
            this with { Held = Held + 1, FirstHeld = Held == 0 ? eventId : FirstHeld, LastHeld = eventId };

        public Window WithoutHeld() => this with { Held = 0, FirstHeld = default, LastHeld = default };
    }
}
