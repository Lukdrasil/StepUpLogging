using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lukdrasil.StepUpLogging.Tests;

public class AuditEventTests
{
    [Fact]
    public void Success_SetsActionActorIdAndOutcome_LeavesEverythingElseDefault()
    {
        var evt = AuditEvent.Success("order.cancel", "user-1");

        Assert.Equal("order.cancel", evt.Action);
        Assert.Equal("user-1", evt.ActorId);
        Assert.Equal(AuditOutcome.Success, evt.Outcome);
        Assert.Equal("user", evt.ActorType);
        Assert.Null(evt.OnBehalfOfId);
        Assert.Null(evt.TenantId);
        Assert.Null(evt.TargetType);
        Assert.Null(evt.TargetId);
        Assert.Null(evt.Reason);
        Assert.Null(evt.Data);
        Assert.Equal(default, evt.TimestampUtc);
        Assert.Null(evt.TraceId);
        Assert.Null(evt.SpanId);
        Assert.Null(evt.SourceIp);
        Assert.Null(evt.UserAgent);
    }

    [Fact]
    public void Failure_SetsMatchingOutcome()
    {
        var evt = AuditEvent.Failure("order.cancel", "user-1");

        Assert.Equal(AuditOutcome.Failure, evt.Outcome);
    }

    [Fact]
    public void Denied_SetsMatchingOutcome()
    {
        var evt = AuditEvent.Denied("order.cancel", "user-1");

        Assert.Equal(AuditOutcome.Denied, evt.Outcome);
    }

    [Fact]
    public void With_OverridesContext_WithoutDisturbingRequiredMembers()
    {
        var original = AuditEvent.Success("order.cancel", "user-1");

        var enriched = original with
        {
            TargetType = "order",
            TargetId = "42",
            Data = new Dictionary<string, object?> { ["channel"] = "support-portal" }
        };

        Assert.Equal("order", enriched.TargetType);
        Assert.Equal("42", enriched.TargetId);
        Assert.Equal("support-portal", enriched.Data!["channel"]);
        Assert.Equal(original.Action, enriched.Action);
        Assert.Equal(original.ActorId, enriched.ActorId);
        Assert.Equal(original.Outcome, enriched.Outcome);
    }

    [Theory]
    [InlineData(nameof(AuditEvent.Action))]
    [InlineData(nameof(AuditEvent.ActorId))]
    [InlineData(nameof(AuditEvent.Outcome))]
    public void RequiredMembers_CarryRequiredMemberAttribute(string propertyName)
    {
        var property = typeof(AuditEvent).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>());
    }

    [Fact]
    public async Task FakeAuditEventSink_ImplementsWriteAsync_AndCanBeAwaited()
    {
        var sink = new FakeAuditEventSink();
        var evt = AuditEvent.Success("order.cancel", "user-1");

        await sink.WriteAsync(evt);

        Assert.Single(sink.Written);
        Assert.Same(evt, sink.Written[0]);
    }

    [Fact]
    public async Task FakeAuditLogger_ImplementsBothAuditAsyncOverloads()
    {
        var logger = new FakeAuditLogger<AuditEventTests>();
        var evt = AuditEvent.Success("order.cancel", "user-1");
        var logInvoked = false;

        await logger.AuditAsync(evt);
        await logger.AuditAsync(evt, _ => logInvoked = true);

        Assert.Equal(2, logger.Audited.Count);
        Assert.True(logInvoked);
    }

    private sealed class FakeAuditEventSink : IAuditEventSink
    {
        public List<AuditEvent> Written { get; } = [];

        public ValueTask WriteAsync(AuditEvent auditEvent)
        {
            Written.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAuditLogger<T> : IAuditLogger<T>
    {
        public List<AuditEvent> Audited { get; } = [];

        public ValueTask AuditAsync(AuditEvent auditEvent)
        {
            Audited.Add(auditEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask AuditAsync(AuditEvent auditEvent, Action<ILogger> log)
        {
            Audited.Add(auditEvent);
            log(NullLoggerFactory.Instance.CreateLogger<T>());
            return ValueTask.CompletedTask;
        }
    }
}
