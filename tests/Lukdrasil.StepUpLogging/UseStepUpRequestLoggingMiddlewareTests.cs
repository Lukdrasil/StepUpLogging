using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class UseStepUpRequestLoggingMiddlewareTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        [Fact(DisplayName = "Middleware_EmitsRequestSummary_WhenAlwaysLogEnabled")]
        public async Task Middleware_EmitsRequestSummary_WhenAlwaysLogEnabled()
        {
            var capture = new CaptureSink();
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    // Register options with AlwaysLogRequestSummary true
                    var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information" };
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton<Serilog.ILogger>(summaryLogger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, summaryLogger));
                })
                .Configure(app =>
                {
                    // Add middleware
                    app.UseStepUpRequestLogging();

                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("ok");
                    });
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            var res = await client.GetAsync("/api/test");
            res.EnsureSuccessStatusCode();

            // Allow some time for logging to propagate
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.True(capture.LastEvent!.Properties.ContainsKey("IsRequestSummary"));
        }
    }
}
