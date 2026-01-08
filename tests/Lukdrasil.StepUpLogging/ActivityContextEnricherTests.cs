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
}

internal sealed class SimpleLogEventPropertyFactory : ILogEventPropertyFactory
{
    public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
    {
        return new LogEventProperty(name, new ScalarValue(value));
    }
}
