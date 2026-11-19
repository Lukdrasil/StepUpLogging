using System.Diagnostics;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Remembers each stuck record (a record with at least one failed delivery attempt in this
/// process) and the record trace its retry Warnings, its dead-letter Critical and its closing
/// Information entry share (ADR 0037). Cycles run one at a time on the drain worker, so a plain
/// dictionary is enough: nothing else touches it concurrently.
/// </summary>
internal sealed class StuckRecordTraces
{
    internal const string ActivityName = "audit.spool.deliver";

    private static readonly ActivitySource Source = new(StepUpLoggingEncryptedSpoolExtensions.AuditDeliveryActivitySourceName, "1.0.0");

    private readonly Dictionary<string, StuckRecord> _records = [];

    /// <summary>
    /// Counts one failed delivery attempt for every record in <paramref name="batch"/> and returns
    /// the head's span, marked failed with <paramref name="reason"/>, in its record trace. A record
    /// that was not stuck yet joins the head's record trace.
    /// </summary>
    public RecordSpan AttemptFailed(IReadOnlyList<SpoolEntry> batch, string reason)
    {
        var known = _records.GetValueOrDefault(batch[0].FilePath);
        var span = Begin(known.Trace, batch[0].FilePath, known.FailedAttempts + 1);
        Activity.Current!.SetStatus(ActivityStatusCode.Error, reason);
        var trace = known.Trace == default ? span.Context : known.Trace;
        foreach (var entry in batch)
        {
            Count(entry.FilePath, trace);
        }

        return span;
    }

    /// <summary>
    /// Forgets the stuck record at <paramref name="spoolFilePath"/> and returns a span in its record
    /// trace, or <see langword="null"/> when the record never failed a delivery attempt.
    /// </summary>
    public RecordSpan? Settled(string spoolFilePath) =>
        _records.Remove(spoolFilePath, out var record) ? Begin(record.Trace, spoolFilePath, record.FailedAttempts) : null;

    private void Count(string spoolFilePath, ActivityContext trace)
    {
        _records[spoolFilePath] = _records.TryGetValue(spoolFilePath, out var known)
            ? known with { FailedAttempts = known.FailedAttempts + 1 }
            : new StuckRecord(trace, 1);
    }

    private static RecordSpan Begin(ActivityContext parent, string spoolFilePath, int failedAttempts)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        var activity = Start(parent);
        activity.SetTag("audit.spool.file", Path.GetFileName(spoolFilePath));
        activity.SetTag("audit.spool.failed_attempts", failedAttempts);
        return new RecordSpan(activity, previous, failedAttempts);
    }

    private static Activity Start(ActivityContext parent) =>
        Source.StartActivity(ActivityName, ActivityKind.Internal, parent) ?? Unobserved(parent);

    private static Activity Unobserved(ActivityContext parent)
    {
        var activity = new Activity(ActivityName);
        activity.SetIdFormat(ActivityIdFormat.W3C);
        if (parent != default)
        {
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        }

        return activity.Start();
    }

    private readonly record struct StuckRecord(ActivityContext Trace, int FailedAttempts);

    /// <summary>
    /// One span in a stuck record's record trace. It is <see cref="Activity.Current"/> until it is
    /// disposed, which hands back the activity that was current before it began.
    /// </summary>
    internal readonly struct RecordSpan(Activity activity, Activity? previous, int failedAttempts) : IDisposable
    {
        /// <summary>The failed delivery attempts of the stuck record, this one included.</summary>
        public int FailedAttempts => failedAttempts;

        /// <summary>The context of this span, whose trace is the stuck record's record trace.</summary>
        public ActivityContext Context => activity.Context;

        /// <summary>Ends the span and restores the activity that was current before it began.</summary>
        public void Dispose()
        {
            activity.Dispose();
            Activity.Current = previous;
        }
    }
}
