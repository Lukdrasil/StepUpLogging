using System.Text.RegularExpressions;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="DroppedRecordLog"/>: a full spool still says at Critical that it is
/// losing audit records, but at a bounded rate — the first drop of a window by name, the rest of
/// the window as one summary (issue #69, ADR 0020 D6).
/// </summary>
public class DroppedRecordLogTests
{
    // Hex letters only, so a digit found in a message can only be the count the test looks for.
    private static readonly Guid First = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Second = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Middle = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Last = new("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid AfterTheWindow = new("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private static readonly SpoolUsage Full = new(Bytes: 4096, Records: 100, FillFraction: 1);

    private static readonly EncryptedSpoolOptions Options = new()
    {
        SpoolDirectory = "/var/spool/audit",
        SpoolMaxEntries = 100,
        SpoolFullRecheckInterval = TimeSpan.FromSeconds(1)
    };

    private static bool NamesTheCount(string message, int count) => Regex.IsMatch(message, $@"\b{count}\b");

    private static bool IsSummaryOf(string message, int count) =>
        NamesTheCount(message, count) && message.Contains(Second.ToString()) && message.Contains(Last.ToString());

    [Fact]
    public void Dropped_FirstInAWindow_LogsCriticalNamingTheRecord()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var log = new DroppedRecordLog(Options, new FakeTimeProvider(), logger);

        log.Dropped(First, Full);

        Assert.Contains(First.ToString(), Assert.Single(logger.MessagesAt(LogLevel.Critical)));
    }

    [Fact]
    public void Dropped_FiftyTimesWithinOneWindow_LogsOneCritical()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var log = new DroppedRecordLog(Options, new FakeTimeProvider(), logger);

        for (var i = 0; i < 50; i++)
        {
            log.Dropped(Guid.CreateVersion7(), Full);
        }

        Assert.Single(logger.MessagesAt(LogLevel.Critical));
    }

    [Fact]
    public void Dropped_FirstAfterTheWindowEnded_LogsASummaryOfTheDropsItHeldBackAndNamesItself()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        var time = new FakeTimeProvider();
        using var log = new DroppedRecordLog(Options, time, logger);
        log.Dropped(First, Full);
        log.Dropped(Second, Full);
        log.Dropped(Middle, Full);
        log.Dropped(Last, Full);

        time.Advance(Options.SpoolFullRecheckInterval);
        log.Dropped(AfterTheWindow, Full);

        var critical = logger.MessagesAt(LogLevel.Critical);
        Assert.Contains(critical, message => IsSummaryOf(message, 3));
        Assert.Contains(critical, message => message.Contains(AfterTheWindow.ToString()));
        Assert.DoesNotContain(critical, message => message.Contains(Middle.ToString()));
    }

    [Fact]
    public void Stored_AfterDropsWereHeldBack_LogsTheSummaryAtOnce()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var log = new DroppedRecordLog(Options, new FakeTimeProvider(), logger);
        log.Dropped(First, Full);
        log.Dropped(Second, Full);
        log.Dropped(Last, Full);

        log.Stored();

        Assert.Contains(logger.MessagesAt(LogLevel.Critical), message => IsSummaryOf(message, 2));
    }

    [Fact]
    public void Stored_WithNothingHeldBack_LogsNothingMore()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var log = new DroppedRecordLog(Options, new FakeTimeProvider(), logger);
        log.Dropped(First, Full);

        log.Stored();

        Assert.Single(logger.MessagesAt(LogLevel.Critical));
    }

    [Fact]
    public void Dispose_AfterDropsWereHeldBack_LogsTheSummarySoNoLossGoesUnreported()
    {
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        var log = new DroppedRecordLog(Options, new FakeTimeProvider(), logger);
        log.Dropped(First, Full);
        log.Dropped(Second, Full);
        log.Dropped(Middle, Full);
        log.Dropped(Last, Full);

        log.Dispose();

        Assert.Contains(logger.MessagesAt(LogLevel.Critical), message => IsSummaryOf(message, 3));
    }
}
