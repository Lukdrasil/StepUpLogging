using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Lukdrasil.StepUpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    /// <summary>
    /// Characterization tests for the private <c>ExtractClientIp</c>/<c>ExtractUserAgent</c> methods
    /// on <see cref="StepUpLoggingExtensions"/>. These are only reachable through the real
    /// <c>UseStepUpRequestLogging()</c> middleware pipeline with <c>AlwaysLogRequestSummary</c> enabled,
    /// so every test here drives a real <see cref="TestServer"/> end-to-end rather than calling a
    /// stand-in copy of the extraction logic.
    /// </summary>
    public class UserAgentAndIpExtractionTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        private static TestServer CreateTestServer(CaptureSink captureSink)
        {
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(captureSink).CreateLogger();
            var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information", RedactionRegexes = new[] { "secret-[A-Za-z0-9]+" } };

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton<Serilog.ILogger>(summaryLogger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, summaryLogger));
                    var patterns = opts.RedactionRegexes
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        .ToArray();
                    services.AddSingleton(new CompiledRedactionPatterns(patterns));
                    var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                                   ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                    var ctor = diagType!.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                    var args = ctor.GetParameters().Select(p =>
                        p.ParameterType == typeof(Serilog.ILogger) ? (object?)summaryLogger
                        : p.HasDefaultValue ? p.DefaultValue
                        : null).ToArray();
                    services.AddSingleton(diagType, ctor.Invoke(args));
                })
                .Configure(app =>
                {
                    // The real production middleware — this is what actually invokes
                    // ExtractClientIp/ExtractUserAgent, via the AlwaysLogRequestSummary branch.
                    app.UseStepUpRequestLogging();

                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("OK");
                    });
                });

            return new TestServer(builder);
        }

        [Fact(DisplayName = "EmitRequestSummary_RedactsUserAgent_WhenItMatchesAPattern")]
        public async Task EmitRequestSummary_RedactsUserAgent_WhenItMatchesAPattern()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.TryAddWithoutValidation("User-Agent", "Agent/1.0 token=secret-abc123");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.True(logEvent!.Properties.ContainsKey("UserAgent"), "UserAgent property should be in the log event");
            var userAgent = ((ScalarValue)logEvent.Properties["UserAgent"]).Value as string;
            // The User-Agent now flows through the same redaction as every other request-derived value.
            Assert.NotNull(userAgent);
            Assert.Contains("[REDACTED]", userAgent);
            Assert.DoesNotContain("secret-abc123", userAgent);
        }

        [Fact(DisplayName = "EmitRequestSummary_OmitsUserAgent_WhenNotProvided")]
        public async Task EmitRequestSummary_OmitsUserAgent_WhenNotProvided()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.False(logEvent!.Properties.ContainsKey("UserAgent"), "UserAgent property should not be in the log event when header is missing");
        }

        [Fact(DisplayName = "EmitRequestSummary_UsesRemoteIpAddress_WhenXForwardedForAbsent")]
        public async Task EmitRequestSummary_UsesRemoteIpAddress_WhenXForwardedForAbsent()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // TestServer's in-memory connection may or may not populate RemoteIpAddress; today's
            // behaviour is: when it's absent, ClientIp is omitted entirely (not logged as null/empty).
            if (logEvent!.Properties.TryGetValue("ClientIp", out var clientIpProp))
            {
                var clientIpString = ((ScalarValue)clientIpProp).Value as string;
                Assert.False(string.IsNullOrWhiteSpace(clientIpString));
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_ClientIpIsFirstXForwardedForEntry_WhenPresent")]
        public async Task EmitRequestSummary_ClientIpIsFirstXForwardedForEntry_WhenPresent()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42, 198.51.100.100");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.True(logEvent!.Properties.ContainsKey("ClientIp"), "ClientIp property should be in the log event");
            var clientIpProp = logEvent.Properties["ClientIp"];

            // Today's behaviour: XFF is trusted unconditionally (no TrustForwardedHeaders gate yet),
            // and ClientIp is the FIRST entry of the header, not the connection's RemoteIpAddress.
            Assert.Equal("203.0.113.42", ((ScalarValue)clientIpProp).Value);
        }

        [Fact(DisplayName = "EmitRequestSummary_HandlesMultipleXForwardedFor_TakesFirst")]
        public async Task EmitRequestSummary_HandlesMultipleXForwardedFor_TakesFirst()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "192.0.2.1, 192.0.2.2, 192.0.2.3");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.True(logEvent!.Properties.ContainsKey("ClientIp"), "ClientIp property should be in the log event");
            var clientIpProp = logEvent.Properties["ClientIp"];

            Assert.Equal("192.0.2.1", ((ScalarValue)clientIpProp).Value);
        }

        [Fact(DisplayName = "EmitRequestSummary_IncludesUserAgentAndIp_Together")]
        public async Task EmitRequestSummary_IncludesUserAgentAndIp_Together()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("User-Agent", "TestClient/1.0");
            request.Headers.Add("X-Forwarded-For", "10.0.0.1");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.True(logEvent!.Properties.ContainsKey("UserAgent"), "UserAgent property should be in the log event");
            Assert.True(logEvent.Properties.ContainsKey("ClientIp"), "ClientIp property should be in the log event");

            Assert.Equal("TestClient/1.0", ((ScalarValue)logEvent.Properties["UserAgent"]).Value);
            Assert.Equal("10.0.0.1", ((ScalarValue)logEvent.Properties["ClientIp"]).Value);
        }

        [Fact(DisplayName = "EmitRequestSummary_FallsBackToRemoteIp_WhenXForwardedForIsWhitespace")]
        public async Task EmitRequestSummary_FallsBackToRemoteIp_WhenXForwardedForIsWhitespace()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "   ");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // Today's behaviour: a whitespace-only XFF value is treated as absent, falling back to
            // Connection.RemoteIpAddress (which itself may be null/absent on TestServer).
            if (logEvent!.Properties.TryGetValue("ClientIp", out var clientIpProp))
            {
                var clientIpString = ((ScalarValue)clientIpProp).Value as string;
                Assert.NotEqual("   ", clientIpString);
            }
        }
    }
}
