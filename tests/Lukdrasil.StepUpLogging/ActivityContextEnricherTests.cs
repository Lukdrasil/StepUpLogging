using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class ActivityContextEnricherTests
{
    [Fact]
    public void Enrich_WithW3CActivity_AddsTraceFlags()
    {
        // Arrange
        var parentActivity = new Activity("Parent");
        parentActivity.Start();

        var activity = new Activity("Child");
        activity.Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        
        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        var enricher = new ActivityContextEnricher();

        // Act
        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        // Assert
        Assert.True(logEvent.Properties.ContainsKey("TraceFlags"));
        activity.Dispose();
        parentActivity.Dispose();
    }

    [Fact]
    public void Enrich_WithoutActivity_DoesNothing()
    {
        // Arrange
        Activity.Current = null;

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        var enricher = new ActivityContextEnricher();

        // Act
        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        // Assert
        // Should not throw and should not add ParentSpanId property
        Assert.DoesNotContain("ParentSpanId", logEvent.Properties.Keys);
    }

    [Fact]
    public void Enrich_WithActivity_AddsTraceContext()
    {
        // Arrange
        var activity = new Activity("TestActivity");
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        activity.Start();

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        var enricher = new ActivityContextEnricher();

        // Act
        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        // Assert
        // Should add TraceFlags from activity
        Assert.True(logEvent.Properties.ContainsKey("TraceFlags"));
        activity.Dispose();
    }

    [Fact]
    public void Enrich_WithChildActivity_AddsParentSpanId()
    {
        // Arrange - create parent activity first
        var parentActivity = new Activity("ParentActivity");
        parentActivity.Start();

        // Create child activity which will have parent span ID
        var childActivity = new Activity("ChildActivity");
        childActivity.Start();

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        var enricher = new ActivityContextEnricher();

        // Act
        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        // Assert
        // Child activity should have parent span ID
        if (childActivity.ParentSpanId != default)
        {
            Assert.True(logEvent.Properties.ContainsKey("ParentSpanId"));
        }

        childActivity.Dispose();
        parentActivity.Dispose();
    }

    [Fact]
    public void Enrich_RootW3CActivity_DoesNotAddParentSpanId()
    {
        Activity.Current = null;
        var activity = new Activity("Root");
        activity.Start();

        Assert.Equal(ActivityIdFormat.W3C, activity.IdFormat);

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        new ActivityContextEnricher().Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.DoesNotContain("ParentSpanId", logEvent.Properties.Keys);
        Assert.True(logEvent.Properties.ContainsKey("TraceFlags"));

        activity.Dispose();
    }

    [Fact]
    public void Enrich_WithNonEmptyTraceState_AddsTraceState()
    {
        Activity.Current = null;
        var activity = new Activity("TraceStateActivity");
        activity.TraceStateString = "vendor=value";
        activity.Start();

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        new ActivityContextEnricher().Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.True(logEvent.Properties.ContainsKey("TraceState"));
        Assert.Equal("vendor=value", ((ScalarValue)logEvent.Properties["TraceState"]).Value);

        activity.Dispose();
    }

    [Fact]
    public void Enrich_WithEmptyTraceState_DoesNotAddTraceState()
    {
        Activity.Current = null;
        var activity = new Activity("NoTraceStateActivity");
        activity.Start();

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: parser.Parse("test"),
            properties: Array.Empty<LogEventProperty>());

        new ActivityContextEnricher().Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.DoesNotContain("TraceState", logEvent.Properties.Keys);

        activity.Dispose();
    }

    [Fact]
    public void Enrich_NonW3CActivity_DoesNotAddProperties()
    {
        Activity.Current = null;
        var savedFormat = Activity.DefaultIdFormat;
        var savedForce = Activity.ForceDefaultIdFormat;
        Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical;
        Activity.ForceDefaultIdFormat = true;

        var activity = new Activity("HierarchicalActivity");
        activity.Start();

        try
        {
            Assert.Equal(ActivityIdFormat.Hierarchical, activity.IdFormat);

            var parser = new MessageTemplateParser();
            var logEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                messageTemplate: parser.Parse("test"),
                properties: Array.Empty<LogEventProperty>());

            new ActivityContextEnricher().Enrich(logEvent, new SimpleLogEventPropertyFactory());

            Assert.Empty(logEvent.Properties);
        }
        finally
        {
            activity.Dispose();
            Activity.DefaultIdFormat = savedFormat;
            Activity.ForceDefaultIdFormat = savedForce;
        }
    }
}

internal sealed class SimpleLogEventPropertyFactory : ILogEventPropertyFactory
{
    public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
    {
        return new LogEventProperty(name, new ScalarValue(value));
    }
}
