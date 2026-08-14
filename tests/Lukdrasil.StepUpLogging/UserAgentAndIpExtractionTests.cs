using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Lukdrasil.StepUpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    /// <summary>
    /// Characterization tests for the private <c>ExtractClientAddresses</c>/<c>ExtractUserAgent</c> methods
    /// on <see cref="StepUpLoggingExtensions"/>. These are only reachable through the real
    /// <c>UseStepUpRequestLogging()</c> middleware pipeline with <c>AlwaysLogRequestSummary</c> enabled,
    /// so every test here drives a real <see cref="TestServer"/> end-to-end rather than calling a
    /// stand-in copy of the extraction logic.
    /// </summary>
    public class UserAgentAndIpExtractionTests
    {
        private const string KnownRemoteIp = "198.51.100.7";

        private sealed class CaptureSink : ILogEventSink
        {
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        private static async Task<IHost> CreateTestServerAsync(CaptureSink captureSink, bool trustForwardedHeaders = false, string? jtiClaim = null, string[]? redactionRegexes = null)
        {
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(captureSink).CreateLogger();
            var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information", TrustForwardedHeaders = trustForwardedHeaders, RedactionRegexes = redactionRegexes ?? new[] { "secret-[A-Za-z0-9]+" } };

            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
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
                        services.AddSingleton(new DiagnosticContext(summaryLogger));
                    })
                    .Configure(app =>
                    {
                        // Give the in-memory connection a deterministic remote address and (optionally) a
                        // jti claim, so the extraction contract can be asserted unambiguously.
                        app.Use(async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress = IPAddress.Parse(KnownRemoteIp);
                            if (jtiClaim is not null)
                            {
                                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("jti", jtiClaim) }, "test"));
                            }
                            await next();
                        });

                        // The real production middleware — this is what actually invokes
                        // ExtractClientAddresses/ExtractUserAgent, via the AlwaysLogRequestSummary branch.
                        app.UseStepUpRequestLogging();

                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    }))
                .Build();

            await host.StartAsync();
            return host;
        }

        private static string? StringProperty(LogEvent logEvent, string name) =>
            logEvent.Properties.TryGetValue(name, out var prop) ? ((ScalarValue)prop).Value as string : null;

        [Fact(DisplayName = "EmitRequestSummary_RedactsUserAgent_WhenItMatchesAPattern")]
        public async Task EmitRequestSummary_RedactsUserAgent_WhenItMatchesAPattern()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

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
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

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
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // XFF absent: ClientIp is the connection's RemoteIpAddress, and no ForwardedFor is emitted.
            Assert.Equal(KnownRemoteIp, StringProperty(logEvent!, "ClientIp"));
            Assert.False(logEvent.Properties.ContainsKey("ForwardedFor"), "ForwardedFor should be absent when X-Forwarded-For is not present");
        }

        [Fact(DisplayName = "EmitRequestSummary_ClientIpIsRemoteIp_NotXForwardedFor_ByDefault")]
        public async Task EmitRequestSummary_ClientIpIsRemoteIp_NotXForwardedFor_ByDefault()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42, 198.51.100.100");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // SECURITY (ADR 0008): with TrustForwardedHeaders unset, the spoofable XFF header must NOT
            // become ClientIp; the connection's RemoteIpAddress wins.
            Assert.Equal(KnownRemoteIp, StringProperty(logEvent!, "ClientIp"));
            Assert.NotEqual("203.0.113.42", StringProperty(logEvent, "ClientIp"));
        }

        [Fact(DisplayName = "EmitRequestSummary_EmitsForwardedFor_WhenXForwardedForPresent_ByDefault")]
        public async Task EmitRequestSummary_EmitsForwardedFor_WhenXForwardedForPresent_ByDefault()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42, 198.51.100.100");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // The raw header is still captured — separately, as ForwardedFor — even though it is untrusted.
            Assert.True(logEvent!.Properties.ContainsKey("ForwardedFor"), "ForwardedFor should carry the raw header when present");
            Assert.Equal("203.0.113.42, 198.51.100.100", StringProperty(logEvent, "ForwardedFor"));
        }

        [Fact(DisplayName = "EmitRequestSummary_ClientIpIsFirstXForwardedForEntry_WhenTrusted")]
        public async Task EmitRequestSummary_ClientIpIsFirstXForwardedForEntry_WhenTrusted()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture, trustForwardedHeaders: true);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42, 198.51.100.100");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // TrustForwardedHeaders=true restores v2 behaviour: the first XFF entry becomes ClientIp.
            Assert.Equal("203.0.113.42", StringProperty(logEvent!, "ClientIp"));
        }

        [Fact(DisplayName = "EmitRequestSummary_TrustedMultipleXForwardedFor_TakesFirstTrimmed")]
        public async Task EmitRequestSummary_TrustedMultipleXForwardedFor_TakesFirstTrimmed()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture, trustForwardedHeaders: true);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "192.0.2.1, 192.0.2.2, 192.0.2.3");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.Equal("192.0.2.1", StringProperty(logEvent!, "ClientIp"));
        }

        [Fact(DisplayName = "EmitRequestSummary_ForwardedForIsRedacted_WhenItMatchesAPattern")]
        public async Task EmitRequestSummary_ForwardedForIsRedacted_WhenItMatchesAPattern()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "secret-abc123");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            var forwardedFor = StringProperty(logEvent!, "ForwardedFor");
            Assert.NotNull(forwardedFor);
            Assert.Contains("[REDACTED]", forwardedFor);
            Assert.DoesNotContain("secret-abc123", forwardedFor);
        }

        [Fact(DisplayName = "EmitRequestSummary_FallsBackToRemoteIp_WhenXForwardedForIsWhitespace")]
        public async Task EmitRequestSummary_FallsBackToRemoteIp_WhenXForwardedForIsWhitespace()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture, trustForwardedHeaders: true);
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "   ");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // A whitespace-only XFF value is treated as absent, falling back to RemoteIpAddress.
            Assert.Equal(KnownRemoteIp, StringProperty(logEvent!, "ClientIp"));
        }

        [Fact(DisplayName = "EmitRequestSummary_RedactsJti_WhenItMatchesAPattern")]
        public async Task EmitRequestSummary_RedactsJti_WhenItMatchesAPattern()
        {
            var capture = new CaptureSink();
            using var host = await CreateTestServerAsync(capture, jtiClaim: "secret-jti999");
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var jti = StringProperty(capture.LastEvent!, "Jti");
            Assert.NotNull(jti);
            Assert.Contains("[REDACTED]", jti);
            Assert.DoesNotContain("secret-jti999", jti);
        }

        [Fact(DisplayName = "EmitRequestSummary_ClientIpFromRemoteIp_IsNotRedacted_WhenUntrustedXForwardedForPresent")]
        public async Task EmitRequestSummary_ClientIpFromRemoteIp_IsNotRedacted_WhenUntrustedXForwardedForPresent()
        {
            var capture = new CaptureSink();
            // A pattern that WOULD match KnownRemoteIp if redaction were applied to it.
            using var host = await CreateTestServerAsync(capture, trustForwardedHeaders: false, redactionRegexes: new[] { @"198\.51\.100\.7" });
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            // ExtractClientAddresses (StepUpLoggingExtensions.cs:731) falls back to the bare, unredacted
            // Connection.RemoteIpAddress here (TrustForwardedHeaders=false ⇒ the XFF entry is never used
            // as ClientIp) — deliberately, because a connection address is not client-supplied and so is
            // not run through CompiledRedactionPatterns.Redact() the way ForwardedFor is.
            Assert.Equal(KnownRemoteIp, StringProperty(logEvent!, "ClientIp"));
            Assert.DoesNotContain("[REDACTED]", StringProperty(logEvent!, "ClientIp")!);
        }

        [Fact(DisplayName = "EmitRequestSummary_ClientIpFromRemoteIp_IsNotRedacted_WhenTrustedButXForwardedForAbsent")]
        public async Task EmitRequestSummary_ClientIpFromRemoteIp_IsNotRedacted_WhenTrustedButXForwardedForAbsent()
        {
            var capture = new CaptureSink();
            // A pattern that WOULD match KnownRemoteIp if redaction were applied to it.
            using var host = await CreateTestServerAsync(capture, trustForwardedHeaders: true, redactionRegexes: new[] { @"198\.51\.100\.7" });
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            // No X-Forwarded-For header: even with TrustForwardedHeaders=true, ExtractClientAddresses
            // falls back to the same unredacted Connection.RemoteIpAddress branch.
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;

            Assert.Equal(KnownRemoteIp, StringProperty(logEvent!, "ClientIp"));
            Assert.DoesNotContain("[REDACTED]", StringProperty(logEvent!, "ClientIp")!);
        }
    }
}
