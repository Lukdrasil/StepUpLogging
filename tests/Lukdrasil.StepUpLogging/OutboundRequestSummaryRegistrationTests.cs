using System.Collections.Concurrent;
using System.Net;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class OutboundRequestSummaryRegistrationTests
{
    private const string ClientName = "billing";

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }

    /// <summary>Records the name of every handler chain the factory builds.</summary>
    private sealed class NameProbe : IHttpMessageHandlerBuilderFilter
    {
        public ConcurrentQueue<string> Names { get; } = new();

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            Names.Enqueue(builder.Name ?? string.Empty);
            next(builder);
        };
    }

    private static IHost Build(bool alwaysLogOutbound, Action<HostApplicationBuilder>? configure = null, RequestSummaryDetail detail = RequestSummaryDetail.Full)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
        });
        builder.AddStepUpLogging(o =>
        {
            o.AlwaysLogOutboundRequestSummary = alwaysLogOutbound;
            o.RequestSummaryDetail = detail;
        });
        configure?.Invoke(builder);
        return builder.Build();
    }

    private static int SummaryHandlers(IHost host, string clientName)
    {
        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        var count = 0;
        while (handler is DelegatingHandler delegating)
        {
            if (delegating is OutboundRequestSummaryHandler)
            {
                count++;
            }

            handler = delegating.InnerHandler!;
        }

        return count;
    }

    private static IHost BuildWithOtlp(string protocol, NameProbe probe)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "500",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
        });
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter>(probe);
        builder.AddStepUpLogging(o =>
        {
            o.EnableOtlpExporter = true;
            o.AlwaysLogOutboundRequestSummary = true;
        });
        var host = builder.Build();
        _ = host.Services.GetService<TracerProvider>();
        _ = host.Services.GetService<MeterProvider>();
        _ = host.Services.GetServices<ILoggerProvider>().ToList();
        return host;
    }

    [Fact]
    public void FlagOff_AddsNoHandler()
    {
        using var host = Build(alwaysLogOutbound: false, b => b.Services.AddHttpClient(ClientName));

        Assert.Equal(0, SummaryHandlers(host, ClientName));
    }

    [Fact]
    public void FlagOn_NamedClient_HasTheHandlerOnce()
    {
        using var host = Build(alwaysLogOutbound: true, b => b.Services.AddHttpClient(ClientName));

        Assert.Equal(1, SummaryHandlers(host, ClientName));
    }

    [Theory]
    [InlineData(DrainWorker.HttpClientName)]
    [InlineData("OtlpTraceExporter")]
    [InlineData("OtlpMetricExporter")]
    [InlineData("OtlpLogExporter")]
    public void FlagOn_LibraryAndOtlpClients_HaveNoHandler(string excludedClient)
    {
        using var host = Build(alwaysLogOutbound: true, b =>
        {
            b.Services.AddHttpClient(ClientName);
            b.Services.AddHttpClient(excludedClient);
        });

        Assert.Equal(1, SummaryHandlers(host, ClientName));
        Assert.Equal(0, SummaryHandlers(host, excludedClient));
    }

    [Fact]
    public async Task FlagOn_WithResilienceHandler_RetriedCallWritesOneSummary()
    {
        var statuses = new ConcurrentQueue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        using var host = Build(alwaysLogOutbound: true, b => b.Services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(
                () => new HttpResponseMessage(statuses.TryDequeue(out var status) ? status : HttpStatusCode.InternalServerError)))
            .AddResilienceHandler("retry", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            })));
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var sink = new CaptureSink();
        host.Services.GetRequiredService<StepUpLoggingController>()
            .SetSummaryLogger(new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger());

        using var response = await client.GetAsync("http://billing.test/a", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = Assert.Single(sink.Events, e => e.Properties.ContainsKey("IsRequestSummary"));
        Assert.Equal(200, ((ScalarValue)summary.Properties["StatusCode"]).Value);
    }

    [Fact]
    public async Task FlagOn_CompactDetail_FactoryClientSummaryHasNoProtocol()
    {
        using var host = Build(
            alwaysLogOutbound: true,
            b => b.Services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK))),
            RequestSummaryDetail.Compact);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var sink = new CaptureSink();
        host.Services.GetRequiredService<StepUpLoggingController>()
            .SetSummaryLogger(new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger());

        using var response = await client.GetAsync("http://billing.test/a", TestContext.Current.CancellationToken);

        var summary = Assert.Single(sink.Events, e => e.Properties.ContainsKey("IsRequestSummary"));
        Assert.False(summary.Properties.ContainsKey("Protocol"));
    }

    [Theory]
    [InlineData("http/protobuf")]
    [InlineData("grpc")]
    public void OtlpExporter_FactoryClientNames_AreAllExcluded(string protocol)
    {
        var probe = new NameProbe();

        using var host = BuildWithOtlp(protocol, probe);

        Assert.All(probe.Names, name => Assert.Contains(name, (IReadOnlySet<string>)OutboundRequestSummaryFilter.ExcludedClients));
    }

    [Fact]
    public void OtlpExporter_HttpProtobuf_BuildsItsClientsThroughTheFactory()
    {
        var probe = new NameProbe();

        using var host = BuildWithOtlp("http/protobuf", probe);

        Assert.NotEmpty(probe.Names);
    }
}
