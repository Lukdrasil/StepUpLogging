using System.Net;
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

        private TestServer CreateTestServer(CaptureSink captureSink)
        {
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(captureSink).CreateLogger();
            var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information" };

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton<Serilog.ILogger>(summaryLogger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, summaryLogger));
                    var patterns = opts.RedactionRegexes
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        .ToArray();
                    services.AddSingleton(new CompiledRedactionPatterns(patterns));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    
                    // Simplified middleware that directly tests extraction
                    app.Use(async (httpContext, next) =>
                    {
                        var controller = httpContext.RequestServices.GetRequiredService<StepUpLoggingController>();
                        var opts2 = httpContext.RequestServices.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
                        
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        await next();
                        sw.Stop();

                        // Extract using the same logic as UseStepUpRequestLogging
                        var userAgent = ExtractUserAgent(httpContext.Request);
                        var clientIp = ExtractClientIp(httpContext);
                        
                        controller.EmitRequestSummary(
                            httpContext.Request.Method,
                            "/test",
                            httpContext.Response?.StatusCode ?? 0,
                            sw.Elapsed.TotalMilliseconds,
                            null,
                            null,
                            null,
                            userAgent,
                            clientIp);
                    });
                    
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/test", async ctx =>
                        {
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
                });

            return new TestServer(builder);
        }

        private static string? ExtractClientIp(HttpContext httpContext)
        {
            try
            {
                if (httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
                {
                    var forwardedForValue = forwardedFor.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(forwardedForValue))
                    {
                        var ips = forwardedForValue.Split(',');
                        if (ips.Length > 0)
                        {
                            var clientIp = ips[0].Trim();
                            if (!string.IsNullOrWhiteSpace(clientIp))
                            {
                                return clientIp;
                            }
                        }
                    }
                }

                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
                if (!string.IsNullOrWhiteSpace(remoteIp))
                {
                    return remoteIp;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractUserAgent(HttpRequest request)
        {
            try
            {
                if (request.Headers.TryGetValue("User-Agent", out var userAgentValue))
                {
                    var userAgent = userAgentValue.FirstOrDefault();
                    return !string.IsNullOrWhiteSpace(userAgent) ? userAgent : null;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_IncludesUserAgent_WhenProvided")]
        public async Task EmitRequestSummary_IncludesUserAgent_WhenProvided()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;
            
            Assert.True(logEvent!.Properties.ContainsKey("UserAgent"), "UserAgent property should be in the log event");
            var userAgentProp = logEvent.Properties["UserAgent"];
            Assert.Contains("Mozilla/5.0", userAgentProp.ToString());
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

        [Fact(DisplayName = "EmitRequestSummary_IncludesClientIp_DirectConnection")]
        public async Task EmitRequestSummary_IncludesClientIp_DirectConnection()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;
            
            // ClientIp property may be present (if connection IP is available)
            if (logEvent!.Properties.ContainsKey("ClientIp"))
            {
                var clientIpProp = logEvent.Properties["ClientIp"];
                var clientIpString = clientIpProp.ToString().Trim('"');
                
                // If present, should be a valid IP address
                Assert.True(IPAddress.TryParse(clientIpString, out _), $"ClientIp should be a valid IP address, got: {clientIpString}");
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_UsesXForwardedFor_WhenProvided")]
        public async Task EmitRequestSummary_UsesXForwardedFor_WhenProvided()
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
            var clientIpString = clientIpProp.ToString().Trim('"');
            
            Assert.Equal("203.0.113.42", clientIpString);
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
            var clientIpString = clientIpProp.ToString().Trim('"');
            
            Assert.Equal("192.0.2.1", clientIpString);
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
            
            var userAgentProp = logEvent.Properties["UserAgent"].ToString().Trim('"');
            var clientIpProp = logEvent.Properties["ClientIp"].ToString().Trim('"');
            
            Assert.Equal("TestClient/1.0", userAgentProp);
            Assert.Equal("10.0.0.1", clientIpProp);
        }

        [Fact(DisplayName = "EmitRequestSummary_HandlesEmptyXForwardedFor")]
        public async Task EmitRequestSummary_HandlesEmptyXForwardedFor()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;
            
            // With empty X-Forwarded-For, may fall back to connection IP if available
            if (logEvent!.Properties.ContainsKey("ClientIp"))
            {
                var clientIpProp = logEvent.Properties["ClientIp"];
                var clientIpString = clientIpProp.ToString().Trim('"');
                
                Assert.True(IPAddress.TryParse(clientIpString, out _), $"ClientIp should be a valid IP address, got: {clientIpString}");
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_HandlesWhitespaceXForwardedFor")]
        public async Task EmitRequestSummary_HandlesWhitespaceXForwardedFor()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("X-Forwarded-For", "   ");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;
            
            // With whitespace X-Forwarded-For, may fall back to connection IP if available
            if (logEvent!.Properties.ContainsKey("ClientIp"))
            {
                var clientIpProp = logEvent.Properties["ClientIp"];
                var clientIpString = clientIpProp.ToString().Trim('"');
                
                Assert.True(IPAddress.TryParse(clientIpString, out _), $"ClientIp should be a valid IP address, got: {clientIpString}");
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_PreservesTraceIdWithUserAgentAndIp")]
        public async Task EmitRequestSummary_PreservesTraceIdWithUserAgentAndIp()
        {
            var capture = new CaptureSink();
            using var server = CreateTestServer(capture);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.Add("User-Agent", "TestAgent/2.0");
            request.Headers.Add("X-Forwarded-For", "172.16.0.1");

            await client.SendAsync(request);

            Assert.NotNull(capture.LastEvent);
            var logEvent = capture.LastEvent;
            
            Assert.True(logEvent!.Properties.ContainsKey("UserAgent"));
            Assert.True(logEvent.Properties.ContainsKey("ClientIp"));
            Assert.Contains("Request finished", logEvent.MessageTemplate.Text);
        }
    }
}

