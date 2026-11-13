using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// ADR 0036: the Serilog OTLP sink waits at most <c>OTEL_EXPORTER_OTLP_TIMEOUT</c> for one export, so disposing the
/// host of a short-lived process does not wait for the 100 s <see cref="System.Net.Http.HttpClient"/> default when
/// the collector accepts the connection and never answers.
/// </summary>
[Collection(OtlpEnvironmentCollection.Name)]
public class OtlpExportTimeoutTests
{
    [Theory]
    [InlineData("http/protobuf")]
    [InlineData("grpc")]
    public void HostDispose_CollectorAcceptsButNeverAnswers_ReturnsWithinTheExportTimeout(string protocol)
    {
        using var collector = new SilentCollector();
        using var environment = new OtlpEnvironment(collector.Endpoint, protocol, timeoutMilliseconds: "500");
        var builder = Host.CreateApplicationBuilder();
        builder.AddStepUpLogging(o => o.EnableConsoleLogging = false);

        var stopwatch = Stopwatch.StartNew();
        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<Serilog.ILogger>().Warning("otlp timeout probe");
        }
        stopwatch.Stop();

        Assert.True(collector.Accepted > 0, "the sink never connected to the collector");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"host.Dispose() took {stopwatch.Elapsed.TotalSeconds:F1} s with {collector.Accepted} connection(s)");
    }

    [Theory]
    [InlineData(null, 10000)]
    [InlineData("", 10000)]
    [InlineData("500", 500)]
    [InlineData(" 2500 ", 2500)]
    [InlineData("0", 10000)]
    [InlineData("-1", 10000)]
    [InlineData("10s", 10000)]
    public void ResolveOtlpTimeout_ReadsMillisecondsOrFallsBackToTheSpecDefault(string? value, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), StepUpLoggingExtensions.ResolveOtlpTimeout(value));
    }

    internal sealed class SilentCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<Socket> _sockets = [];
        private int _accepted;

        public SilentCollector()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        _sockets.Add(await _listener.AcceptSocketAsync());
                        Interlocked.Increment(ref _accepted);
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public int Accepted => Volatile.Read(ref _accepted);

        public void Dispose()
        {
            _listener.Stop();
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }
    }

    private sealed class OtlpEnvironment : IDisposable
    {
        private static readonly string[] Names =
            ["OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_TIMEOUT"];
        private readonly string?[] _previous = Array.ConvertAll(Names, Environment.GetEnvironmentVariable);

        public OtlpEnvironment(string endpoint, string protocol, string timeoutMilliseconds)
        {
            Environment.SetEnvironmentVariable(Names[0], endpoint);
            Environment.SetEnvironmentVariable(Names[1], protocol);
            Environment.SetEnvironmentVariable(Names[2], timeoutMilliseconds);
        }

        public void Dispose()
        {
            for (var i = 0; i < Names.Length; i++)
            {
                Environment.SetEnvironmentVariable(Names[i], _previous[i]);
            }
        }
    }
}
