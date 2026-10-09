using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins what <see cref="StepUpLoggingController.EmitRequestSummary"/> writes to the summary logger (property
/// order, omitted optionals, value conversion, enrichers, trace context, level gate) while each optional field
/// it carries costs a fraction of the logger chain it used to build. Serilog's <c>MaximumStringLength</c> does
/// not truncate plain scalar strings, so the summary's path and user agent stay whole under it.
/// </summary>
public class RequestSummaryAllocationCutTests
{
    private const string Method = "GET";
    private const string Path = "/api/orders/12345";
    private const int StatusCode = 200;
    private const double ElapsedMs = 12.5;
    private const string QueryString = "?page=2";
    private const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64)";
    private const string ClientIp = "203.0.113.7";
    private const string Jti = "jti-0001";
    private const string ForwardedFor = "198.51.100.1";
    private const int OptionalFieldCount = 6;
    private const int SummariesPerRun = 100;
    // Measured on the ForContext chain (af/perf-prebuffer-lock @ 490a54d), Serilog 4.3.1, net10.0 x64.
    private const long HeadBytesPerOptionalField = 304;
    private const long MaxBytesPerOptionalField = HeadBytesPerOptionalField * 60 / 100;

    private static readonly IReadOnlyDictionary<string, object?> RouteParameters =
        new Dictionary<string, object?> { ["id"] = "12345" };

    /// <summary>Keeps only the last event, so collecting adds no allocation of its own.</summary>
    private sealed class LastEventSink : ILogEventSink
    {
        public LogEvent? LastEvent { get; private set; }
        public int Count { get; private set; }

        public void Emit(LogEvent logEvent)
        {
            LastEvent = logEvent;
            Count++;
        }
    }

    private static StepUpLoggingController Controller(LastEventSink sink, Func<LoggerConfiguration, LoggerConfiguration>? configure = null)
    {
        var configuration = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink);
        var summaryLogger = (configure?.Invoke(configuration) ?? configuration).CreateLogger();
        return new StepUpLoggingController(new StepUpLoggingOptions(), summaryLogger);
    }

    private static void EmitFull(StepUpLoggingController controller) =>
        controller.EmitRequestSummary(
            Method, Path, StatusCode, ElapsedMs,
            queryString: QueryString, routeParameters: RouteParameters, userAgent: UserAgent,
            clientIp: ClientIp, jti: Jti, forwardedFor: ForwardedFor);

    private static void EmitMinimal(StepUpLoggingController controller) =>
        controller.EmitRequestSummary(Method, Path, StatusCode, ElapsedMs);

    private static long AllocatedBytesOfSecondRun(Action run)
    {
        run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void EmitRequestSummary_AllFields_KeepsThePropertyOrder()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink);

        EmitFull(controller);

        Assert.Equal(
            ["Method", "Path", "StatusCode", "ElapsedMs", "ForwardedFor", "Jti", "ClientIp", "UserAgent", "RouteParameters", "QueryString", "IsRequestSummary"],
            sink.LastEvent!.Properties.Keys);
    }

    [Fact]
    public void EmitRequestSummary_EmptyOptionals_LeavesThemOut()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink);

        controller.EmitRequestSummary(
            Method, Path, StatusCode, ElapsedMs,
            queryString: string.Empty, routeParameters: new Dictionary<string, object?>(), userAgent: string.Empty,
            clientIp: string.Empty, jti: string.Empty, forwardedFor: string.Empty);

        Assert.Equal(["Method", "Path", "StatusCode", "ElapsedMs", "IsRequestSummary"], sink.LastEvent!.Properties.Keys);
    }

    [Fact]
    public void EmitRequestSummary_RouteParameters_IsADictionaryValue()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink);

        EmitFull(controller);

        var routeParameters = Assert.IsType<DictionaryValue>(sink.LastEvent!.Properties["RouteParameters"]);
        Assert.Equal("\"12345\"", Assert.Single(routeParameters.Elements).Value.ToString());
    }

    [Fact]
    public void EmitRequestSummary_SummaryLoggerEnrichers_StillRun()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink, c => c.Enrich.WithProperty("Application", "orders"));

        EmitFull(controller);

        Assert.Equal("\"orders\"", sink.LastEvent!.Properties["Application"].ToString());
        Assert.Equal("true", sink.LastEvent.Properties["IsRequestSummary"].ToString(), ignoreCase: true);
    }

    [Fact]
    public void EmitRequestSummary_InsideAnActivity_CarriesItsTraceIdAndSpanId()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink);
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        EmitFull(controller);

        Assert.Equal(activity.TraceId, sink.LastEvent!.TraceId);
        Assert.Equal(activity.SpanId, sink.LastEvent.SpanId);
    }

    [Fact]
    public void EmitRequestSummary_MaximumStringLength_LeavesPathAndUserAgentWhole()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink, c => c.Destructure.ToMaximumStringLength(10));

        EmitFull(controller);

        Assert.Equal($"\"{Path}\"", sink.LastEvent!.Properties["Path"].ToString());
        Assert.Equal($"\"{UserAgent}\"", sink.LastEvent.Properties["UserAgent"].ToString());
    }

    [Fact]
    public void EmitRequestSummary_SummaryLoggerAtWarning_WritesNothing()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink, c => c.MinimumLevel.Warning());

        EmitFull(controller);

        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void EmitRequestSummary_EachOptionalField_AllocatesUnder60PercentOfTheLoggerChain()
    {
        var sink = new LastEventSink();
        using var controller = Controller(sink);

        var fullBytes = AllocatedBytesOfSecondRun(() =>
        {
            for (var i = 0; i < SummariesPerRun; i++) EmitFull(controller);
        });
        var minimalBytes = AllocatedBytesOfSecondRun(() =>
        {
            for (var i = 0; i < SummariesPerRun; i++) EmitMinimal(controller);
        });
        var bytesPerOptionalField = (fullBytes - minimalBytes) / SummariesPerRun / OptionalFieldCount;

        Assert.True(
            bytesPerOptionalField < MaxBytesPerOptionalField,
            $"{bytesPerOptionalField} B per optional field, expected under {MaxBytesPerOptionalField} B (60 % of {HeadBytesPerOptionalField} B at HEAD)");
    }
}
