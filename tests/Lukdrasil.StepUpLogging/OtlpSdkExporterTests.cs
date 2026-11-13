using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Lukdrasil.StepUpLogging.Tests;

[Collection(OtlpEnvironmentCollection.Name)]
public class OtlpSdkExporterTests
{
    private const string ProbeName = "Lukdrasil.StepUpLogging.Tests.OtlpProbe";

    public enum AppExporter { None, UseOtlpExporterBefore, UseOtlpExporterAfter, AddOtlpExporterBefore, AddOtlpExporterAfter }

    [Theory]
    [InlineData(AppExporter.None)]
    [InlineData(AppExporter.UseOtlpExporterBefore)]
    [InlineData(AppExporter.UseOtlpExporterAfter)]
    [InlineData(AppExporter.AddOtlpExporterBefore)]
    [InlineData(AppExporter.AddOtlpExporterAfter)]
    public void SdkRegisteredBeforeAddStepUpLogging_ExportsTracesAndMetricsOnce(AppExporter appExporter)
    {
        using var collector = new RecordingCollector();
        using var environment = new OtlpEnvironment(collector.Endpoint);
        var builder = Host.CreateApplicationBuilder();
        var sdk = builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddSource(ProbeName))
            .WithMetrics(m => m.AddMeter(ProbeName));
        AddAppExporter(builder, appExporter, before: true);
        builder.AddStepUpLogging(o => o.EnableConsoleLogging = false);
        AddAppExporter(builder, appExporter, before: false);

        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<TracerProvider>();
            host.Services.GetRequiredService<MeterProvider>();
            using var source = new ActivitySource(ProbeName);
            using var meter = new Meter(ProbeName);
            source.StartActivity("probe")?.Dispose();
            meter.CreateCounter<long>("probe").Add(1);
        }

        Assert.Equal(1, collector.Count("/v1/traces"));
        Assert.Equal(1, collector.Count("/v1/metrics"));
    }

    [Fact]
    public void SdkRegisteredBeforeAddStepUpLogging_OtlpDisabled_ExportsNoTracesOrMetrics()
    {
        using var collector = new RecordingCollector();
        using var environment = new OtlpEnvironment(collector.Endpoint);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddSource(ProbeName))
            .WithMetrics(m => m.AddMeter(ProbeName));
        builder.AddStepUpLogging(o => { o.EnableConsoleLogging = false; o.EnableOtlpExporter = false; });

        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<TracerProvider>();
            host.Services.GetRequiredService<MeterProvider>();
            using var source = new ActivitySource(ProbeName);
            source.StartActivity("probe")?.Dispose();
        }

        Assert.Equal(0, collector.Count("/v1/traces"));
        Assert.Equal(0, collector.Count("/v1/metrics"));
    }

    private static void AddAppExporter(HostApplicationBuilder builder, AppExporter appExporter, bool before)
    {
        switch (appExporter)
        {
            case AppExporter.UseOtlpExporterBefore when before:
            case AppExporter.UseOtlpExporterAfter when !before:
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
                break;
            case AppExporter.AddOtlpExporterBefore when before:
            case AppExporter.AddOtlpExporterAfter when !before:
                builder.Services.AddOpenTelemetry()
                    .WithTracing(t => t.AddOtlpExporter())
                    .WithMetrics(m => m.AddOtlpExporter());
                break;
        }
    }

    private sealed class RecordingCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<string> _paths = new();

        public RecordingCollector()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var context = await _listener.GetContextAsync();
                        _paths.Enqueue(context.Request.Url!.AbsolutePath);
                        await context.Request.InputStream.CopyToAsync(Stream.Null);
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        public string Endpoint { get; }

        public int Count(string path) => _paths.Count(p => p == path);

        public void Dispose() => _listener.Close();
    }

    private sealed class OtlpEnvironment : IDisposable
    {
        private static readonly string[] Names = ["OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL"];
        private readonly string?[] _previous = Array.ConvertAll(Names, Environment.GetEnvironmentVariable);

        public OtlpEnvironment(string endpoint)
        {
            Environment.SetEnvironmentVariable(Names[0], endpoint);
            Environment.SetEnvironmentVariable(Names[1], "http/protobuf");
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

[CollectionDefinition(Name)]
public sealed class OtlpEnvironmentCollection
{
    public const string Name = "OTLP environment variables";
}
