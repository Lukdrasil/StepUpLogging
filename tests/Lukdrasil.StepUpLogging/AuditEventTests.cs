using System.Reflection;
using System.Runtime.CompilerServices;

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
}
