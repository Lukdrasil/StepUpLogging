using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class RequestLevelClassificationTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    /// <summary>A request whose connection the client already dropped.</summary>
    private sealed class AbortedLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = new(canceled: true);
        public void Abort() { }
    }

    private static async Task<LogEventLevel?> LevelOf(
        StepUpLoggingOptions opts, string path, Action<IApplicationBuilder> configure)
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    services.AddSingleton(new CompiledRedactionPatterns(Array.Empty<System.Text.RegularExpressions.Regex>()));
                    var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                                   ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                    var ctor = diagType!.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                    var args = ctor.GetParameters().Select(p =>
                        p.ParameterType == typeof(Serilog.ILogger) ? (object?)logger
                        : p.HasDefaultValue ? p.DefaultValue
                        : null).ToArray();
                    services.AddSingleton(diagType, ctor.Invoke(args));
                })
                .Configure(app =>
                {
                    app.UseStepUpRequestLogging();
                    configure(app);
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            try
            {
                await client.GetAsync(path);
            }
            catch
            {
                // The pipeline rethrows the handler's exception; the completion event is still logged.
            }

            lock (capture.Events)
            {
                return capture.Events
                    .FirstOrDefault(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal))
                    ?.Level;
            }
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task ClientAbortedRequest_IsNotError()
    {
        var level = await LevelOf(new StepUpLoggingOptions(), "/api/slow", app =>
            app.Run(ctx =>
            {
                ctx.Features.Set<IHttpRequestLifetimeFeature>(new AbortedLifetimeFeature());
                throw new OperationCanceledException(ctx.RequestAborted);
            }));

        Assert.Equal(LogEventLevel.Information, level);
    }

    [Fact]
    public async Task ClientAbortedRequest_WithoutException_IsNotError()
    {
        var level = await LevelOf(new StepUpLoggingOptions(), "/api/slow", app =>
            app.Run(ctx =>
            {
                ctx.Features.Set<IHttpRequestLifetimeFeature>(new AbortedLifetimeFeature());
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }));

        Assert.Equal(LogEventLevel.Information, level);
    }

    [Fact]
    public async Task ApplicationException_IsStillError_EvenWhenClientAlsoAborted()
    {
        var level = await LevelOf(new StepUpLoggingOptions(), "/api/explode", app =>
            app.Run(ctx =>
            {
                ctx.Features.Set<IHttpRequestLifetimeFeature>(new AbortedLifetimeFeature());
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public async Task ServerErrorStatus_IsError_ByDefault()
    {
        var level = await LevelOf(new StepUpLoggingOptions(), "/api/upstream", app =>
            app.Run(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }));

        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public async Task ServerErrorStatus_IsWarning_WhenNotTreatedAsError()
    {
        var opts = new StepUpLoggingOptions { TreatServerErrorStatusAsError = false };

        var level = await LevelOf(opts, "/api/upstream", app =>
            app.Run(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }));

        Assert.Equal(LogEventLevel.Warning, level);
    }

    [Fact]
    public async Task ThrownException_IsError_WhenNotTreatedAsError()
    {
        var opts = new StepUpLoggingOptions { TreatServerErrorStatusAsError = false };

        var level = await LevelOf(opts, "/api/explode", app =>
            app.Run(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public async Task ExcludedPath_StaysVerbose()
    {
        var opts = new StepUpLoggingOptions { ExcludePaths = ["/healthz"] };

        var level = await LevelOf(opts, "/healthz", app =>
            app.Run(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }));

        Assert.Equal(LogEventLevel.Verbose, level);
    }
}
