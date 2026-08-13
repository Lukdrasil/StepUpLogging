using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="AuditLogger{T}"/> against the real step-up pipeline: the audit write
/// reaches the sink regardless of the level switch, the companion log is emitted only after a
/// successful write, and enrichment reuses the request-logging extraction rules.
/// </summary>
public class AuditLoggerTests
{
    private const string RemoteIp = "198.51.100.7";

    private sealed class RecordingAuditSink : IAuditEventSink
    {
        public List<AuditEvent> Written { get; } = [];

        public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
        {
            Written.Add(auditEvent);
            return ValueTask.FromResult(AuditWriteResult.Stored);
        }
    }

    /// <summary>A sink that completes asynchronously, so an implementation that fails to await the write is caught.</summary>
    private sealed class AsyncJournalingAuditSink(List<string> journal) : IAuditEventSink
    {
        public async ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
        {
            await Task.Delay(20);
            journal.Add("audit");
            return AuditWriteResult.Stored;
        }
    }

    /// <summary>A sink that yields before throwing, so a fire-and-forget write cannot observe the failure.</summary>
    private sealed class ThrowingAuditSink(Exception failure) : IAuditEventSink
    {
        public async ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
        {
            await Task.Yield();
            throw failure;
        }
    }

    /// <summary>A sink that stores nothing and reports <paramref name="result"/>, whatever it is.</summary>
    private sealed class ResultReportingAuditSink(AuditWriteResult result) : IAuditEventSink
    {
        public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent) => ValueTask.FromResult(result);
    }

    /// <summary>
    /// Sums the measurements of the <c>StepUpLogging.Audit</c> meter's counters. The outcome tag is
    /// read as a <see cref="string"/>, so a counter tagged with anything else records as untagged.
    /// </summary>
    private sealed class AuditMeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<(string Instrument, string? Outcome), long> _totals = [];

        public AuditMeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "StepUpLogging.Audit")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                string? outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome") outcome = tag.Value as string;
                }

                lock (_totals)
                {
                    var key = (instrument.Name, outcome);
                    _totals[key] = _totals.TryGetValue(key, out var total) ? total + measurement : measurement;
                }
            });
            _listener.Start();
        }

        public long Total(string instrumentName, string? outcome = null)
        {
            lock (_totals)
            {
                return _totals.TryGetValue((instrumentName, outcome), out var total) ? total : 0;
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class Harness : IDisposable
    {
        public CollectingSink GatedOutput { get; } = new();
        public CollectingSink BypassOutput { get; } = new();
        public StepUpLoggingController Controller { get; }
        public AuditLogger<AuditLoggerTests> AuditLogger { get; }

        /// <summary>A logger on the audit logger's own category, for contrasting an ordinary event with the companion log.</summary>
        public ILogger<AuditLoggerTests> AuditCategoryLogger { get; }

        private readonly StepUpSink _stepUpSink;
        private readonly ImmediateSink _immediateSink;
        private readonly SerilogLoggerFactory _loggerFactory;

        public Harness(
            IAuditEventSink sink,
            HttpContext? httpContext = null,
            string[]? redactionRegexes = null,
            bool trustForwardedHeaders = false,
            string[]? neverStepUpCategories = null)
        {
            var options = new StepUpLoggingOptions
            {
                BaseLevel = "Warning",
                StepUpLevel = "Information",
                DurationSeconds = 10,
                TrustForwardedHeaders = trustForwardedHeaders,
                RedactionRegexes = redactionRegexes ?? [],
                NeverStepUpCategories = neverStepUpCategories ?? []
            };

            Controller = new StepUpLoggingController(options);

            var gatedInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(GatedOutput).CreateLogger();
            var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(BypassOutput).CreateLogger();
            _stepUpSink = new StepUpSink(gatedInner, Controller.LevelSwitch, Controller.BaseLevel, options.NeverStepUpCategories);
            _immediateSink = new ImmediateSink(bypassLogger);

            var root = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(_stepUpSink)
                .WriteTo.Sink(_immediateSink)
                .CreateLogger();
            _loggerFactory = new SerilogLoggerFactory(root, dispose: true);

            var patterns = new CompiledRedactionPatterns(
                options.RedactionRegexes.Select(StepUpLoggingExtensions.CompilePattern).ToArray());

            AuditCategoryLogger = _loggerFactory.CreateLogger<AuditLoggerTests>();

            AuditLogger = new AuditLogger<AuditLoggerTests>(
                sink,
                AuditCategoryLogger,
                new HttpContextAccessor { HttpContext = httpContext },
                Options.Create(options),
                patterns);
        }

        public void Dispose()
        {
            _loggerFactory.Dispose();
            _immediateSink.Dispose();
            _stepUpSink.Dispose();
            Controller.Dispose();
        }
    }

    private static HttpContext HttpContextWith(string? userAgent = null, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(RemoteIp);
        if (userAgent is not null) context.Request.Headers.UserAgent = userAgent;
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    [Fact]
    public async Task AuditAsync_WritesToSink_WhenStepUpIsInactive()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        Assert.False(harness.Controller.IsSteppedUp);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        var written = Assert.Single(sink.Written);
        Assert.Equal("order.cancel", written.Action);
        Assert.Equal(AuditOutcome.Success, written.Outcome);
    }

    [Fact]
    public async Task AuditAsync_WritesToSink_WhenStepUpIsActive()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        harness.Controller.Trigger();
        Assert.True(harness.Controller.IsSteppedUp);

        await harness.AuditLogger.AuditAsync(AuditEvent.Denied("order.cancel", "user-42", "user"));

        var written = Assert.Single(sink.Written);
        Assert.Equal(AuditOutcome.Denied, written.Outcome);
    }

    [Fact]
    public async Task AuditAsync_StampsTimestampUtc_AtCallTime()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        var before = DateTimeOffset.UtcNow;
        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));
        var after = DateTimeOffset.UtcNow;

        var written = Assert.Single(sink.Written);
        Assert.InRange(written.TimestampUtc, before, after);
        Assert.Equal(TimeSpan.Zero, written.TimestampUtc.Offset);
    }

    [Fact]
    public async Task AuditAsync_OverwritesCallerSuppliedTimestamp()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);
        var callerStamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user") with { TimestampUtc = callerStamp });

        Assert.NotEqual(callerStamp, Assert.Single(sink.Written).TimestampUtc);
    }

    [Fact]
    public async Task AuditAsync_StampsAUuidV7EventId_PerCall()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);
        var auditEvent = AuditEvent.Success("order.cancel", "user-42", "user");

        await harness.AuditLogger.AuditAsync(auditEvent);
        await harness.AuditLogger.AuditAsync(auditEvent);

        // Version 7 rather than merely "a Guid": the receiver deduplicates on this value and sorts
        // by it once a drained spool arrives out of order, and only a UUIDv7 is time-ordered.
        Assert.All(sink.Written, written => Assert.Equal(7, written.EventId.Version));
        Assert.NotEqual(sink.Written[0].EventId, sink.Written[1].EventId);
    }

    [Fact]
    public async Task AuditAsync_OverwritesCallerSuppliedEventId()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);
        var callerEventId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user") with { EventId = callerEventId });

        Assert.NotEqual(callerEventId, Assert.Single(sink.Written).EventId);
    }

    [Fact]
    public async Task AuditAsync_TakesTraceAndSpanIds_FromCurrentActivity()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        using var activitySource = new ActivitySource("AuditLoggerTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AuditLoggerTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("audited-operation");
        Assert.NotNull(activity);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        var written = Assert.Single(sink.Written);
        Assert.Equal(activity.TraceId.ToString(), written.TraceId);
        Assert.Equal(activity.SpanId.ToString(), written.SpanId);
    }

    [Fact]
    public async Task AuditAsync_LeavesRequestContextNull_OutsideAnHttpRequest()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("job.run", "system", "service"));

        var written = Assert.Single(sink.Written);
        Assert.Null(written.SourceIp);
        Assert.Null(written.UserAgent);
    }

    [Fact]
    public async Task AuditAsync_TakesSourceIpFromConnection_Unredacted()
    {
        var sink = new RecordingAuditSink();
        // A pattern that would match the remote address if SourceIp were redacted.
        using var harness = new Harness(sink, HttpContextWith(), redactionRegexes: [@"198\.51\.100\.7"]);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        Assert.Equal(RemoteIp, Assert.Single(sink.Written).SourceIp);
    }

    [Fact]
    public async Task AuditAsync_TakesSourceIpFromForwardedFor_WhenForwardedHeadersAreTrusted()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(
            sink,
            HttpContextWith(forwardedFor: "203.0.113.42, 198.51.100.100"),
            trustForwardedHeaders: true);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        Assert.Equal("203.0.113.42", Assert.Single(sink.Written).SourceIp);
    }

    [Fact]
    public async Task AuditAsync_RedactsSourceIpTakenFromForwardedFor()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(
            sink,
            HttpContextWith(forwardedFor: "203.0.113.42"),
            redactionRegexes: [@"203\.0\.113\.\d+"],
            trustForwardedHeaders: true);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        // Redaction inside SourceIp is asymmetric by origin, not by field: a forwarded address is
        // client-supplied and is redacted, whereas the connection address above is not. Inherited
        // from ExtractClientAddresses (ADR 0008) rather than decided here.
        Assert.Equal("[REDACTED]", Assert.Single(sink.Written).SourceIp);
    }

    [Fact]
    public async Task AuditAsync_IgnoresForwardedFor_WhenForwardedHeadersAreNotTrusted()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink, HttpContextWith(forwardedFor: "203.0.113.42"));

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        Assert.Equal(RemoteIp, Assert.Single(sink.Written).SourceIp);
    }

    [Fact]
    public async Task AuditAsync_RedactsUserAgent()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(
            sink,
            HttpContextWith(userAgent: "Agent/1.0 token=secret-abc123"),
            redactionRegexes: ["secret-[A-Za-z0-9]+"]);

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        var userAgent = Assert.Single(sink.Written).UserAgent;
        Assert.NotNull(userAgent);
        Assert.Contains("[REDACTED]", userAgent);
        Assert.DoesNotContain("secret-abc123", userAgent);
    }

    [Fact]
    public async Task AuditAsync_LeavesCallerSuppliedFieldsUnredacted()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink, HttpContextWith(), redactionRegexes: ["secret-[A-Za-z0-9]+"]);
        var data = new Dictionary<string, object?> { ["note"] = "secret-data99" };

        await harness.AuditLogger.AuditAsync(AuditEvent.Denied("order.cancel", "secret-actor11", "secret-kind44") with
        {
            TargetType = "order",
            TargetId = "secret-target22",
            Reason = "secret-reason33",
            Data = data
        });

        var written = Assert.Single(sink.Written);
        Assert.Equal("secret-actor11", written.ActorId);
        Assert.Equal("secret-kind44", written.ActorType);
        Assert.Equal("secret-target22", written.TargetId);
        Assert.Equal("secret-reason33", written.Reason);
        Assert.Same(data, written.Data);
    }

    [Fact]
    public async Task AuditAsync_WritesAudit_BeforeCompanionLog()
    {
        var journal = new List<string>();
        using var harness = new Harness(new AsyncJournalingAuditSink(journal));

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user"),
            _ => journal.Add("log"));

        Assert.Equal(new[] { "audit", "log" }, journal);
    }

    [Fact]
    public async Task AuditAsync_CompanionLog_BypassesTheLevelSwitch()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        // Information is below the Warning base level, so an ordinary event would be dropped.
        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user"),
            log => log.LogInformation("order {OrderId} cancelled", "order-7"));

        Assert.Empty(harness.GatedOutput.Events);
        var logged = Assert.Single(harness.BypassOutput.Events);
        Assert.Equal(LogEventLevel.Information, logged.Level);
        Assert.True(logged.Properties.ContainsKey(LogProperties.IsImmediate));
    }

    [Fact]
    public async Task AuditAsync_ReachesTheSinkAndExportsTheCompanionLog_WhenItsOwnCategoryNeverStepsUp()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink, neverStepUpCategories: [typeof(AuditLoggerTests).FullName!]);

        harness.Controller.Trigger();

        harness.AuditCategoryLogger.LogInformation("ordinary event from the audited category");

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user"),
            log => log.LogInformation("order {OrderId} cancelled", "order-7"));

        // The empty gated output proves the deny-list is live: it pins this category to Warning
        // even while stepped up, so the ordinary Information event above is dropped. It still
        // suppresses neither audit path — the audit write never enters Serilog at all, and the
        // companion log, which StepUpSink drops on that very same pinned gate, is carried out by
        // ImmediateSink instead, because the audit logger marks it IsImmediate.
        Assert.Empty(harness.GatedOutput.Events);
        Assert.Single(sink.Written);
        var exported = Assert.Single(harness.BypassOutput.Events);
        Assert.Equal("order {OrderId} cancelled", exported.MessageTemplate.Text);
        Assert.Equal(LogEventLevel.Information, exported.Level);
    }

    [Fact]
    public async Task AuditAsync_CompanionLogScope_EndsAfterTheDelegateReturns()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);
        Microsoft.Extensions.Logging.ILogger? borrowed = null;

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user"),
            log =>
            {
                borrowed = log;
                log.LogInformation("inside");
            });

        borrowed!.LogWarning("outside");

        Assert.Single(harness.BypassOutput.Events);
        Assert.Single(harness.GatedOutput.Events);
    }

    [Fact]
    public async Task AuditAsync_WhenSinkThrows_PropagatesTheExceptionUnchanged()
    {
        var failure = new InvalidOperationException("audit store unreachable");
        using var harness = new Harness(new ThrowingAuditSink(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user")));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task AuditAsync_WhenSinkThrows_DoesNotInvokeTheCompanionLog()
    {
        using var harness = new Harness(new ThrowingAuditSink(new InvalidOperationException("audit store unreachable")));
        var logInvoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(
                AuditEvent.Success("order.cancel", "user-42", "user"),
                _ => logInvoked = true));

        Assert.False(logInvoked);
        Assert.Empty(harness.BypassOutput.Events);
    }

    [Fact]
    public async Task AuditAsync_WhenSinkThrows_CountsExactlyOneWriteFailure()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new ThrowingAuditSink(new InvalidOperationException("audit store unreachable")));

        var before = recorder.Total("audit_write_failures_total");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user")));

        Assert.Equal(before + 1, recorder.Total("audit_write_failures_total"));
    }

    [Fact]
    public async Task AuditAsync_WhenSinkThrows_DoesNotCountTheEvent()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new ThrowingAuditSink(new InvalidOperationException("audit store unreachable")));

        var before = recorder.Total("audit_events_total", nameof(AuditOutcome.Success));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user")));

        Assert.Equal(before, recorder.Total("audit_events_total", nameof(AuditOutcome.Success)));
    }

    [Fact]
    public async Task AuditAsync_WhenSinkDropsTheRecord_CountsTheDrop_WithoutCountingTheEvent()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new ResultReportingAuditSink(AuditWriteResult.Dropped));

        var droppedBefore = recorder.Total("audit_events_dropped_total");
        var writtenBefore = recorder.Total("audit_events_total", nameof(AuditOutcome.Success));

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        // audit_events_total means records written, and the operator's "audit stopped" alarm reads
        // it: counting a dropped record there would make the alarm healthiest while audit is lost.
        Assert.Equal(droppedBefore + 1, recorder.Total("audit_events_dropped_total"));
        Assert.Equal(writtenBefore, recorder.Total("audit_events_total", nameof(AuditOutcome.Success)));
    }

    [Fact]
    public async Task AuditAsync_WhenSinkDropsTheRecord_DoesNotInvokeTheCompanionLog()
    {
        using var harness = new Harness(new ResultReportingAuditSink(AuditWriteResult.Dropped));
        var logInvoked = false;

        await harness.AuditLogger.AuditAsync(
            AuditEvent.Success("order.cancel", "user-42", "user"),
            _ => logInvoked = true);

        // A companion log asserting an action for which no audit record exists is the thing the
        // write-then-log order exists to prevent, and a dropped record is exactly that case.
        Assert.False(logInvoked);
        Assert.Empty(harness.BypassOutput.Events);
    }

    [Fact]
    public async Task AuditAsync_WhenSinkStoresTheRecord_DoesNotCountADrop()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new RecordingAuditSink());

        var droppedBefore = recorder.Total("audit_events_dropped_total");

        await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        Assert.Equal(droppedBefore, recorder.Total("audit_events_dropped_total"));
    }

    [Theory]
    [InlineData(default(AuditWriteResult))]
    [InlineData((AuditWriteResult)99)]
    public async Task AuditAsync_WhenSinkReportsAnUnrecognizedResult_ThrowsNamingTheSink(AuditWriteResult result)
    {
        using var harness = new Harness(new ResultReportingAuditSink(result));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user")));

        // Choosing a branch on the sink's behalf would mean asserting either that the record exists
        // or that it is gone, and there is no way to know which — so the sink is named instead.
        Assert.Contains(nameof(ResultReportingAuditSink), thrown.Message);
    }

    [Fact]
    public async Task AuditAsync_WhenSinkReportsAnUnrecognizedResult_CountsNeitherAWriteFailureNorTheEvent()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new ResultReportingAuditSink(default));

        var failuresBefore = recorder.Total("audit_write_failures_total");
        var writtenBefore = recorder.Total("audit_events_total", nameof(AuditOutcome.Success));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user")));

        // audit_write_failures_total means the sink threw while writing; here the write may well
        // have succeeded, and only the sink's answer about it is broken.
        Assert.Equal(failuresBefore, recorder.Total("audit_write_failures_total"));
        Assert.Equal(writtenBefore, recorder.Total("audit_events_total", nameof(AuditOutcome.Success)));
    }

    [Theory]
    [InlineData(AuditOutcome.Success, "Success")]
    [InlineData(AuditOutcome.Failure, "Failure")]
    [InlineData(AuditOutcome.Denied, "Denied")]
    public async Task AuditAsync_CountsOneEvent_TaggedWithTheOutcome(AuditOutcome outcome, string expectedTag)
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new RecordingAuditSink());
        var auditEvent = AuditEvent.Success("order.cancel", "user-42", "user") with { Outcome = outcome };

        var before = recorder.Total("audit_events_total", expectedTag);

        await harness.AuditLogger.AuditAsync(auditEvent);

        Assert.Equal(before + 1, recorder.Total("audit_events_total", expectedTag));
        Assert.Equal(0, recorder.Total("audit_events_total"));
    }

    [Fact]
    public async Task AuditAsync_TagsAnUndefinedOutcome_AsUnknown()
    {
        using var recorder = new AuditMeterRecorder();
        using var harness = new Harness(new RecordingAuditSink());
        var auditEvent = AuditEvent.Success("order.cancel", "user-42", "user") with { Outcome = (AuditOutcome)99 };

        var before = recorder.Total("audit_events_total", "Unknown");

        await harness.AuditLogger.AuditAsync(auditEvent);

        // The outcome tag is the counter's only dimension, so its cardinality stays bounded even
        // when a caller casts an undefined value into it.
        Assert.Equal(before + 1, recorder.Total("audit_events_total", "Unknown"));
        Assert.Equal(0, recorder.Total("audit_events_total", "99"));
    }

    [Fact]
    public async Task AuditAsync_PropagatesAnExceptionFromTheCompanionLogDelegate()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);
        var failure = new InvalidOperationException("broken LoggerMessage call");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.AuditLogger.AuditAsync(
                AuditEvent.Success("order.cancel", "user-42", "user"),
                _ => throw failure));

        Assert.Same(failure, thrown);
        // The audit record is already durable — the log delegate is the caller's own bug.
        Assert.Single(sink.Written);
    }

    [Fact]
    public async Task AuditAsync_RejectsANullCompanionLogDelegate_WithoutWriting()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await harness.AuditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"), null!));

        Assert.Empty(sink.Written);
    }

    [Fact]
    public async Task AuditAsync_RejectsANullAuditEvent()
    {
        var sink = new RecordingAuditSink();
        using var harness = new Harness(sink);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await harness.AuditLogger.AuditAsync(null!));

        Assert.Empty(sink.Written);
    }
}
