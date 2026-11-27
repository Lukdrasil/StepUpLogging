using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins that the log scopes open where an exception is thrown reach the events that log it after the scopes were
/// disposed: the request completion event, the exception handler event and a caught, wrapped exception.
/// </summary>
public class ExceptionScopeCaptureTests
{
    private static async Task<LogEvent[]> RunAsync(bool? captureScopesOnException)
    {
        var key = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:BaseLevel"] = "Information",
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
            ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
            ["Serilog:WriteTo:0:Args:key"] = key,
        };
        if (captureScopesOnException is { } capture)
        {
            settings["SerilogStepUp:CaptureScopesOnException"] = capture.ToString();
        }

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.AddStepUpLogging();

            await using (var app = builder.Build())
            {
                app.UseExceptionHandler(e => e.Run(ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return Task.CompletedTask;
                }));
                app.UseStepUpRequestLogging();
                app.MapGet("/unhandled", async (ILogger<ExceptionScopeCaptureTests> log) =>
                {
                    using (log.BeginScope(new Dictionary<string, object> { ["OrderId"] = 42 }))
                    using (LogContext.PushProperty("Tenant", "t1"))
                    {
                        await Task.Yield();
                        throw new InvalidOperationException("unhandled");
                    }
                });
                app.MapGet("/caught", (ILogger<ExceptionScopeCaptureTests> log) =>
                {
                    try
                    {
                        using (log.BeginScope(new Dictionary<string, object> { ["OrderId"] = 43 }))
                        {
                            throw new InvalidOperationException("caught");
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        log.LogError(new ApplicationException("wrapped", ex), "Order failed");
                    }

                    return "ok";
                });
                app.MapGet("/rethrown", (ILoggerFactory factory) =>
                {
                    var log = factory.CreateLogger("ScopeCapture.Rethrown");
                    try
                    {
                        using (log.BeginScope(new Dictionary<string, object> { ["Site"] = "wrapper" }))
                        {
                            try
                            {
                                using (log.BeginScope(new Dictionary<string, object> { ["Site"] = "deep" }))
                                {
                                    throw new InvalidOperationException("deep");
                                }
                            }
                            catch (InvalidOperationException ex)
                            {
                                throw new ApplicationException("wrapper", ex);
                            }
                        }
                    }
                    catch (ApplicationException ex)
                    {
                        log.LogError(ex, "Rethrown failed");
                    }

                    return "ok";
                });
                app.MapGet("/aggregate", (ILoggerFactory factory) =>
                {
                    var log = factory.CreateLogger("ScopeCapture.Aggregate");
                    var failures = new List<Exception>();
                    foreach (var key in new[] { "First", "Second" })
                    {
                        try
                        {
                            using (log.BeginScope(new Dictionary<string, object> { [key] = key }))
                            {
                                throw new InvalidOperationException(key);
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            failures.Add(ex);
                        }
                    }

                    log.LogError(new AggregateException(failures), "Aggregate failed");
                    return "ok";
                });
                app.MapGet("/current", (ILoggerFactory factory) =>
                {
                    var log = factory.CreateLogger("ScopeCapture.Current");
                    using (log.BeginScope(new Dictionary<string, object> { ["Step"] = 1 }))
                    {
                        try
                        {
                            using (log.BeginScope(new Dictionary<string, object> { ["Step"] = 2 }))
                            {
                                throw new InvalidOperationException("current");
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            log.LogError(ex, "Current failed");
                        }
                    }

                    return "ok";
                });

                await app.StartAsync(TestContext.Current.CancellationToken);
                using var client = app.GetTestClient();
                (await client.GetAsync("/unhandled", TestContext.Current.CancellationToken)).Dispose();
                (await client.GetAsync("/caught", TestContext.Current.CancellationToken)).Dispose();
                (await client.GetAsync("/rethrown", TestContext.Current.CancellationToken)).Dispose();
                (await client.GetAsync("/aggregate", TestContext.Current.CancellationToken)).Dispose();
                (await client.GetAsync("/current", TestContext.Current.CancellationToken)).Dispose();
                await app.StopAsync(TestContext.Current.CancellationToken);
            }

            return KeyedCaptureSink.Events(key).Where(e => e.Exception is not null).ToArray();
        }
        finally
        {
            KeyedCaptureSink.Forget(key);
        }
    }

    private static LogEvent EventFrom(LogEvent[] events, string sourceContext) =>
        Assert.Single(events, e => e.Properties.TryGetValue("SourceContext", out var sc) && sc is ScalarValue { Value: string s } && s == sourceContext);

    private static object? ScalarOf(LogEvent evt, string property) =>
        evt.Properties.TryGetValue(property, out var value) ? Assert.IsType<ScalarValue>(value).Value : null;

    [Fact]
    public async Task ScopeAtThrowSite_IsOnTheRequestCompletionEvent()
    {
        var evt = EventFrom(await RunAsync(null), "Serilog.AspNetCore.RequestLoggingMiddleware");

        Assert.Equal(42, ScalarOf(evt, "OrderId"));
        Assert.Equal("t1", ScalarOf(evt, "Tenant"));
    }

    [Fact]
    public async Task ScopeAtThrowSite_IsOnTheExceptionHandlerEvent()
    {
        var evt = EventFrom(await RunAsync(null), "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");

        Assert.Equal(42, ScalarOf(evt, "OrderId"));
        Assert.Equal("t1", ScalarOf(evt, "Tenant"));
    }

    [Fact]
    public async Task ScopeAtThrowSite_OfTheInnerException_IsOnTheEventLoggingTheWrapper()
    {
        var evt = EventFrom(await RunAsync(null), typeof(ExceptionScopeCaptureTests).FullName!);

        Assert.Equal(43, ScalarOf(evt, "OrderId"));
    }

    [Fact]
    public async Task ScopeAtThrowSite_OfTheDeepestThrow_WinsOverTheRethrownWrapper()
    {
        var evt = EventFrom(await RunAsync(null), "ScopeCapture.Rethrown");

        Assert.Equal("deep", ScalarOf(evt, "Site"));
    }

    [Fact]
    public async Task ScopeAtThrowSite_OfEveryAggregatedException_IsOnTheEventLoggingTheAggregate()
    {
        var evt = EventFrom(await RunAsync(null), "ScopeCapture.Aggregate");

        Assert.Equal("First", ScalarOf(evt, "First"));
        Assert.Equal("Second", ScalarOf(evt, "Second"));
    }

    [Fact]
    public async Task ScopeAtLogSite_KeepsItsValue_OverTheThrowSiteScope()
    {
        var evt = EventFrom(await RunAsync(null), "ScopeCapture.Current");

        Assert.Equal(1, ScalarOf(evt, "Step"));
    }

    [Fact]
    public async Task CaptureScopesOnExceptionOff_LeavesTheThrowSiteScopeOut()
    {
        var events = await RunAsync(false);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.False(e.Properties.ContainsKey("OrderId")));
    }
}
