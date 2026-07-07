using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class RequestSummaryOnExceptionTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public async Task RequestSummary_IsEmittedWith500_WhenHandlerThrows()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true };

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
                    app.Run(_ => throw new InvalidOperationException("boom"));
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            try
            {
                await client.GetAsync("/api/explode");
            }
            catch
            {
                // The pipeline rethrows the handler's exception; that is expected. The summary
                // must still have been emitted in the finally block.
            }
            await Task.Delay(50);

            var summary = capture.Events.FirstOrDefault(e =>
                e.Properties.TryGetValue(LogProperties.IsRequestSummary, out var flag)
                && flag is ScalarValue { Value: true });

            Assert.NotNull(summary);
            Assert.True(summary!.Properties.TryGetValue("StatusCode", out var status));
            Assert.Equal("500", status!.ToString());
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
