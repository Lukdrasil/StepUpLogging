using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lukdrasil.StepUpLogging.Tests;

public class AuditEventTests
{
    [Fact]
    public void Success_SetsActionActorIdActorTypeAndOutcome_LeavesEverythingElseDefault()
    {
        var evt = AuditEvent.Success("order.cancel", "user-1", "user");

        Assert.Equal("order.cancel", evt.Action);
        Assert.Equal("user-1", evt.ActorId);
        Assert.Equal("user", evt.ActorType);
        Assert.Equal(AuditOutcome.Success, evt.Outcome);
        Assert.Null(evt.OnBehalfOfId);
        Assert.Null(evt.TenantId);
        Assert.Null(evt.TargetType);
        Assert.Null(evt.TargetId);
        Assert.Null(evt.Reason);
        Assert.Null(evt.Data);
        Assert.Null(evt.OldValues);
        Assert.Null(evt.NewValues);
        Assert.Equal(Guid.Empty, evt.EventId);
        Assert.Equal(default, evt.TimestampUtc);
        Assert.Null(evt.TraceId);
        Assert.Null(evt.SpanId);
        Assert.Null(evt.SourceIp);
        Assert.Null(evt.UserAgent);
    }

    [Fact]
    public void Failure_SetsActionActorIdActorTypeAndMatchingOutcome()
    {
        var evt = AuditEvent.Failure("payment.capture", "user-2", "user");

        Assert.Equal("payment.capture", evt.Action);
        Assert.Equal("user-2", evt.ActorId);
        Assert.Equal("user", evt.ActorType);
        Assert.Equal(AuditOutcome.Failure, evt.Outcome);
    }

    /// <summary>
    /// A denial of a service token is the case the <c>"user"</c> default used to record as a human:
    /// the factory has to carry whatever actor kind the call site names, and nothing else.
    /// </summary>
    [Fact]
    public void Denied_ForAServiceToken_SetsActionActorIdActorTypeAndMatchingOutcome()
    {
        var evt = AuditEvent.Denied("report.export", "svc-report-exporter", "service");

        Assert.Equal("report.export", evt.Action);
        Assert.Equal("svc-report-exporter", evt.ActorId);
        Assert.Equal("service", evt.ActorType);
        Assert.Equal(AuditOutcome.Denied, evt.Outcome);
    }

    [Fact]
    public void With_CarriesCallerSuppliedOldAndNewValues()
    {
        var oldValues = new Dictionary<string, object?> { ["role"] = "viewer" };
        var newValues = new Dictionary<string, object?> { ["role"] = "editor" };

        var evt = AuditEvent.Success("user.role.change", "admin-1", "user") with
        {
            OldValues = oldValues,
            NewValues = newValues
        };

        Assert.Same(oldValues, evt.OldValues);
        Assert.Same(newValues, evt.NewValues);
    }

    [Fact]
    public void With_OverridesContext_WithoutDisturbingRequiredMembers()
    {
        var original = AuditEvent.Success("order.cancel", "user-1", "user");

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
        Assert.Equal(original.ActorType, enriched.ActorType);
        Assert.Equal(original.Outcome, enriched.Outcome);
    }

    [Theory]
    [InlineData(nameof(AuditEvent.Action))]
    [InlineData(nameof(AuditEvent.ActorId))]
    [InlineData(nameof(AuditEvent.ActorType))]
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
        var evt = AuditEvent.Success("order.cancel", "user-1", "user");

        await sink.WriteAsync(evt);

        Assert.Single(sink.Written);
        Assert.Same(evt, sink.Written[0]);
    }

    [Fact]
    public async Task FakeAuditLogger_ImplementsBothAuditAsyncOverloads()
    {
        var logger = new FakeAuditLogger<AuditEventTests>();
        var evt = AuditEvent.Success("order.cancel", "user-1", "user");
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
