using System.Diagnostics;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// <see cref="StuckRecordTraces"/> on its own: a record span runs in the record trace, never under
/// whatever activity was current, and hands that activity back when it ends (ADR 0037).
/// </summary>
public class StuckRecordTracesTests
{
    [Fact]
    public void StuckRecordTraces_SpanOverAnAmbientActivity_RunsInTheRecordTraceAndRestoresTheAmbientOnDispose()
    {
        using var ambient = new Activity("ambient").Start();
        var traces = new StuckRecordTraces();
        ActivityTraceId failedAttemptTrace;
        ActivityTraceId settledTrace;

        using (traces.AttemptFailed([new SpoolEntry("a.env", null)], "the audit endpoint answered 503 ServiceUnavailable"))
        {
            failedAttemptTrace = Activity.Current!.TraceId;
        }

        var currentAfterFailedAttempt = Activity.Current;
        using (traces.Settled("a.env"))
        {
            settledTrace = Activity.Current!.TraceId;
        }

        var currentAfterSettled = Activity.Current;

        Assert.NotEqual(ambient.TraceId, failedAttemptTrace);
        Assert.NotEqual(default, failedAttemptTrace);
        Assert.Equal(failedAttemptTrace, settledTrace);
        Assert.Same(ambient, currentAfterFailedAttempt);
        Assert.Same(ambient, currentAfterSettled);
    }
}
