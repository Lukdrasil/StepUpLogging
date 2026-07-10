using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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

public class RequestBodyCaptureTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static TestServer BuildServer(CaptureSink capture, Serilog.ILogger logger, out StepUpLoggingOptions opts, int? maxBodyCaptureBytes = null)
    {
        opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.AlwaysOn,        // IsSteppedUp == true
            CaptureRequestBody = true,
            RedactionRegexes = new[] { "secret-[A-Za-z0-9]+" }
        };
        if (maxBodyCaptureBytes is int max) opts.MaxBodyCaptureBytes = max;
        var options = opts;

        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Options.Create(options));
                services.AddSingleton(logger);
                services.AddSingleton(sp => new StepUpLoggingController(options, logger));
                var patterns = options.RedactionRegexes
                    .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    .ToArray();
                services.AddSingleton(new CompiledRedactionPatterns(patterns));
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
                app.Run(async ctx =>
                {
                    // Consume the body exactly like a real endpoint would — this is what the old
                    // implementation could not survive (it buffered too late, after this read).
                    // leaveOpen so we don't dispose Request.Body (model binding doesn't either).
                    var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                    var received = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync($"len={received.Length}");
                });
            });

        return new TestServer(builder);
    }

    private static string? FindRequestBody(CaptureSink capture) =>
        capture.Events
            .Where(e => e.Properties.ContainsKey("RequestBody"))
            .Select(e => e.Properties["RequestBody"].ToString().Trim('"'))
            .LastOrDefault();

    [Fact]
    public async Task RequestBody_IsCaptured_WhenHandlerConsumesIt()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var server = BuildServer(capture, logger, out _);
            using var client = server.CreateClient();

            var body = "{\"user\":\"bob\",\"token\":\"secret-abc123\"}";
            var response = await client.PostAsync("/api/test", new StringContent(body, Encoding.UTF8, "application/json"));
            var echoed = await response.Content.ReadAsStringAsync();
            await Task.Delay(50);

            // The handler still received the full body (buffering did not steal it).
            Assert.Equal($"len={body.Length}", echoed);

            var captured = FindRequestBody(capture);
            Assert.NotNull(captured);
            Assert.Contains("[REDACTED]", captured);
            Assert.DoesNotContain("secret-abc123", captured);
            Assert.Contains("bob", captured);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task RequestBody_IsEmptySentinel_WhenBodyIsZeroLength()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var server = BuildServer(capture, logger, out _);
            using var client = server.CreateClient();

            // Zero-length body on a POST while stepped up.
            var response = await client.PostAsync("/api/test", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
            var echoed = await response.Content.ReadAsStringAsync();
            await Task.Delay(50);

            Assert.Equal("len=0", echoed);

            var captured = FindRequestBody(capture);
            Assert.Equal("[EMPTY]", captured);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task RequestBody_StraddlingSecret_IsMasked_BeforeTruncation()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            const int limit = 40;
            using var server = BuildServer(capture, logger, out _, maxBodyCaptureBytes: limit);
            using var client = server.CreateClient();

            // The secret starts at offset 34, so a naive read of the first 40 chars would slice it
            // mid-token ("...xxsecret" with the hyphen past the cut), the pattern would no longer
            // match, and the "secret" prefix would leak. Redaction must run over the margin first.
            var body = new string('x', 34) + "secret-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var response = await client.PostAsync("/api/test", new StringContent(body, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            await Task.Delay(50);

            var captured = FindRequestBody(capture);
            Assert.NotNull(captured);
            Assert.DoesNotContain("secret", captured);
            Assert.True(captured!.Length <= limit, $"captured length {captured.Length} must not exceed {limit}");
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    private sealed class ThrowingOnReadBody : Stream
    {
        private readonly Stream _inner;
        public ThrowingOnReadBody(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated read failure");

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RequestBody_IsUnavailableSentinel_WhenBodyReadThrows()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.AlwaysOn,
                CaptureRequestBody = true,
                RedactionRegexes = new[] { "secret-[A-Za-z0-9]+" }
            };

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    var patterns = opts.RedactionRegexes
                        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        .ToArray();
                    services.AddSingleton(new CompiledRedactionPatterns(patterns));
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
                    // Swap the request body for a stream that throws on Read, but reports
                    // CanSeek = true so it passes the enricher's gate and hits the read itself.
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Request.EnableBuffering();
                        ctx.Request.Body = new ThrowingOnReadBody(ctx.Request.Body);
                        await next();
                    });
                    app.UseStepUpRequestLogging();
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("ok");
                    });
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            var response = await client.PostAsync("/api/test", new StringContent("{\"x\":1}", Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            await Task.Delay(50);

            var captured = FindRequestBody(capture);
            Assert.Equal("[UNAVAILABLE]", captured);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
