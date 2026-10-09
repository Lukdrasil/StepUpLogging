using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What the pipeline costs when a real output sink sits behind it, up to the moment the sink has written
/// or sent everything: a rolling file, or OTLP over gRPC to a local OpenTelemetry Collector
/// (<c>docs/performance/otlp-collector.yaml</c>). Three scenarios: warnings exported at the base level,
/// an Error that flushes the pre-error buffer, and requests through the middleware while stepped up.
/// Each invocation builds a host and disposes it at the end, which flushes the asynchronous output
/// sinks, so the time includes the wait for the exporter. The host is built outside the timing.
/// <see cref="EmptyHostDisposed"/> is the part of that time that does not depend on the events: disposing
/// a host that logged nothing.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Exporter")]
[InvocationCount(1, 1)]
public class ExporterBenchmarks
{
    private const int WarningsPerInvoke = 1000;
    private const int ErrorFlushesPerInvoke = 100;
    private const int RequestsPerInvoke = 200;
    private const int EventsPerErrorFlush = 2;
    private const int EventsPerRequest = 2;
    private const int StepUpNotices = 1;
    private const int StepUpWaitMilliseconds = 2000;
    private const string ExporterFolder = "stepup-bench-exporter";
    private const string LogFilePattern = "exporter*.json";
    private const string DefaultCollectorEndpoint = "http://localhost:4317";
    private const string CollectorMetricsUrl = "http://localhost:8888/metrics";
    private const string AcceptedLogRecords = "otelcol_receiver_accepted_log_records";
    private const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";

    private static readonly HttpClient Collector = new();

    private string _logDirectory = null!;
    private IHost _host = null!;
    private RequestDelegate _pipeline = null!;

    /// <summary>The output sink behind the pipeline.</summary>
    public enum ExporterKind
    {
        /// <summary>A rolling file of compact JSON, as <c>AddStepUpLogging(logFilePath: ...)</c> writes it.</summary>
        File,

        /// <summary>OTLP over gRPC to the collector on <c>localhost:4317</c>.</summary>
        Otlp,
    }

    /// <summary>The output sink behind the pipeline.</summary>
    [Params(ExporterKind.File, ExporterKind.Otlp)]
    public ExporterKind Exporter { get; set; }

    /// <summary>Runs every scenario once at full size and checks that the exporter received every event it exported.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        Environment.SetEnvironmentVariable(EndpointVariable, Environment.GetEnvironmentVariable(EndpointVariable) ?? DefaultCollectorEndpoint);
        Environment.SetEnvironmentVariable(ProtocolVariable, Environment.GetEnvironmentVariable(ProtocolVariable) ?? "grpc");
        _logDirectory = FreshLogDirectory();
        var delivered = await Delivered();
        var exported = BenchmarkFixtures.ExportedEvents.Received;

        StartHost();
        WarningExported();
        StartHost();
        ErrorFlush();
        StartSteppedUpHost();
        await SteppedUpRequest();

        var expected = WarningsPerInvoke + (ErrorFlushesPerInvoke * EventsPerErrorFlush) + (RequestsPerInvoke * EventsPerRequest) + StepUpNotices;
        var exportedByScenarios = BenchmarkFixtures.ExportedEvents.Received - exported;
        BenchmarkFixtures.Require(exportedByScenarios == expected, $"the pipeline exported {exportedByScenarios} events, expected {expected}");
        var received = await Delivered() - delivered;
        BenchmarkFixtures.Require(received >= expected, $"the {Exporter} exporter received {received} of {expected} events");
    }

    /// <summary>Removes the log files.</summary>
    [GlobalCleanup]
    public void Cleanup() => DeleteLogDirectory();

    /// <summary>Builds the host for the warning and Error scenarios and the empty one.</summary>
    [IterationSetup(Targets = [nameof(WarningExported), nameof(ErrorFlush), nameof(EmptyHostDisposed)])]
    public void StartHost() => _host = BenchmarkFixtures.BuildHost(HostSettings(), logFilePath: LogFilePath());

    /// <summary>Builds the host for the request scenario, with the middleware in front and logging stepped up; the step-up writes one notice of its own.</summary>
    [IterationSetup(Target = nameof(SteppedUpRequest))]
    public void StartSteppedUpHost()
    {
        StartHost();
        var app = new ApplicationBuilder(_host.Services);
        app.UseStepUpRequestLogging();
        app.Run(_ => Task.CompletedTask);
        _pipeline = app.Build();
        var controller = _host.Services.GetRequiredService<StepUpLoggingController>();
        controller.Trigger();
        BenchmarkFixtures.Require(SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, StepUpWaitMilliseconds), "the controller did not step logging up");
    }

    /// <summary>A host that logged nothing, disposed: the shutdown cost every other row of this class includes once per invocation.</summary>
    [Benchmark]
    public void EmptyHostDisposed() => _host.Dispose();

    /// <summary>Warnings at the base level, each exported; reported per event.</summary>
    [Benchmark(OperationsPerInvoke = WarningsPerInvoke)]
    public void WarningExported()
    {
        var logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(BenchmarkFixtures.OrderHandlerContext);
        Repeat(WarningsPerInvoke, () => logger.LogWarning(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value"));
        _host.Dispose();
    }

    /// <summary>An Information event held in the pre-error buffer, then an Error that flushes it; both reach the exporter. Reported per pair.</summary>
    [Benchmark(OperationsPerInvoke = ErrorFlushesPerInvoke)]
    public void ErrorFlush()
    {
        var logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(BenchmarkFixtures.OrderHandlerContext);
        using var trace = BenchmarkFixtures.StartTrace();
        Repeat(ErrorFlushesPerInvoke, () => HoldThenFail(logger));
        _host.Dispose();
    }

    /// <summary>A request through the middleware while stepped up: the request event and the summary. Reported per request.</summary>
    [Benchmark(OperationsPerInvoke = RequestsPerInvoke)]
    public async Task SteppedUpRequest()
    {
        var context = BenchmarkFixtures.Request(headerCount: 4);
        for (var i = 0; i < RequestsPerInvoke; i++)
        {
            await _pipeline(context);
        }

        _host.Dispose();
    }

    /// <summary>
    /// A fresh directory for the log files, short on purpose: the shared file sink names a mutex after the
    /// path and Linux refuses a name of about 250 characters, which the build output path under
    /// BenchmarkDotNet's generated project exceeds. The file output is not what this suite measures: it
    /// measures the pipeline up to the sink, so the temp directory serves.
    /// </summary>
    private static string FreshLogDirectory()
    {
        var root = Environment.GetEnvironmentVariable(SpoolBenchmarkSupport.SpoolRootVariable) is { Length: > 0 } configured
            ? configured
            : Path.GetTempPath();
        var directory = Path.Combine(root, ExporterFolder);
        DeleteDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void DeleteLogDirectory() => DeleteDirectory(_logDirectory);

    private static void HoldThenFail(Microsoft.Extensions.Logging.ILogger logger)
    {
        logger.LogInformation(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");
        logger.LogError(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");
    }

    private static void Repeat(int count, Action action)
    {
        for (var i = 0; i < count; i++)
        {
            action();
        }
    }

    private static async Task<double> AcceptedByCollector()
    {
        var metrics = await Collector.GetStringAsync(CollectorMetricsUrl);
        return metrics.Split('\n')
            .Where(line => line.StartsWith(AcceptedLogRecords, StringComparison.Ordinal))
            .Sum(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture));
    }

    private IEnumerable<KeyValuePair<string, string?>> HostSettings() =>
    [
        BenchmarkFixtures.StepUpSetting("EnableOtlpExporter", (Exporter == ExporterKind.Otlp).ToString()),
        BenchmarkFixtures.StepUpSetting("NeverTriggerCategories:0", "MyApp"),
        BenchmarkFixtures.StepUpSetting("AlwaysLogRequestSummary", "true"),
        BenchmarkFixtures.StepUpSetting("DurationSeconds", "86400"),
    ];

    private string? LogFilePath() => Exporter == ExporterKind.File ? Path.Combine(_logDirectory, "exporter-.json") : null;

    private async Task<double> Delivered() => Exporter == ExporterKind.File ? LinesWritten() : await AcceptedByCollector();

    private double LinesWritten() =>
        Directory.Exists(_logDirectory)
            ? Directory.GetFiles(_logDirectory, LogFilePattern).Sum(file => File.ReadLines(file).Count())
            : 0;
}
