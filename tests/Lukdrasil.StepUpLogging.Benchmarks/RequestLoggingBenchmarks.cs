using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What one request costs through the middleware <c>UseStepUpRequestLogging()</c> adds, over an endpoint
/// that does nothing: with the pipeline stepped up and not, with and without the request summary, and
/// with few and many request headers. Five redaction patterns are configured, as in a host that redacts.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RequestLoggingBenchmarks
{
    private const int StepUpWaitMilliseconds = 2000;

    private IHost _host = null!;
    private RequestDelegate _pipeline = null!;
    private DefaultHttpContext _context = null!;

    /// <summary>Whether the controller has stepped logging up to Information before the requests run.</summary>
    [Params(false, true)]
    public bool Stepped { get; set; }

    /// <summary>How many request headers the request carries on top of the user agent and forwarded-for header.</summary>
    [Params(4, 32)]
    public int HeaderCount { get; set; }

    /// <summary>Whether <c>AlwaysLogRequestSummary</c> is on.</summary>
    [Params(false, true)]
    public bool AlwaysLogRequestSummary { get; set; }

    /// <summary>Builds the pipeline, steps it up when asked, and checks how many events one request exports.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _host = BenchmarkFixtures.BuildHost(
        [
            .. BenchmarkFixtures.RedactionSettings(),
            BenchmarkFixtures.StepUpSetting("AlwaysLogRequestSummary", AlwaysLogRequestSummary.ToString()),
            BenchmarkFixtures.StepUpSetting("DurationSeconds", "86400"),
        ]);
        var app = new ApplicationBuilder(_host.Services);
        app.UseStepUpRequestLogging();
        app.Run(_ => Task.CompletedTask);
        _pipeline = app.Build();
        _context = BenchmarkFixtures.Request(HeaderCount);
        StepUpWhenAsked();

        var expected = (Stepped ? 1 : 0) + (AlwaysLogRequestSummary ? 1 : 0);
        var exported = await ExportedByOneRequest();
        BenchmarkFixtures.Require(exported == expected, $"one request exported {exported} events, expected {expected}");
    }

    /// <summary>Disposes the host.</summary>
    [GlobalCleanup]
    public void Cleanup() => _host.Dispose();

    /// <summary>One request through the middleware.</summary>
    [Benchmark]
    public Task HandleRequest() => _pipeline(_context);

    private async Task<int> ExportedByOneRequest()
    {
        var before = BenchmarkFixtures.ExportedEvents.Received;
        await HandleRequest();
        return BenchmarkFixtures.ExportedEvents.Received - before;
    }

    private void StepUpWhenAsked()
    {
        if (!Stepped)
        {
            return;
        }

        var controller = _host.Services.GetRequiredService<StepUpLoggingController>();
        controller.Trigger();
        var steppedUp = SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, StepUpWaitMilliseconds);
        BenchmarkFixtures.Require(steppedUp, "the controller did not step logging up");
    }
}
