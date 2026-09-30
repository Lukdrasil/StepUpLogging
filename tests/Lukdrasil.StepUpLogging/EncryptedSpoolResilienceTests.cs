using System.Net;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The delivery client on a real host whose consumer applies
/// <c>ConfigureHttpClientDefaults(h =&gt; h.AddStandardResilienceHandler())</c> to every client (issue #33):
/// the inherited handler must not retry, time out or log on the drain worker's behalf, while a
/// handler the consumer adds after <see cref="StepUpLoggingEncryptedSpoolExtensions.AddEncryptedSpoolAuditSink"/> stays.
/// </summary>
public class EncryptedSpoolResilienceTests
{
    /// <summary>The audit receiver behind the delivery client's primary handler, counting what reaches it.</summary>
    private sealed class StubReceiver(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(Interlocked.Increment(ref _sends) - 1, cancellationToken);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, LogLevel Level)> _entries = [];

        public IReadOnlyList<string> CategoriesAt(LogLevel level)
        {
            lock (_entries)
            {
                return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Category)];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class CategoryLogger(string category, RecordingLoggerProvider provider) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider._entries)
                {
                    provider._entries.Add((category, logLevel));
                }
            }
        }
    }

    private static StubReceiver FailingOnceThenOk() =>
        new((sendIndex, _) => Task.FromResult(new HttpResponseMessage(sendIndex == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));

    private static StubReceiver AnsweringOkAfter(TimeSpan delay) =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    /// <summary>A spool below the temporary root, so its dead-letter sibling is thrown away with it.</summary>
    private static string SpoolDirectoryIn(TempSpoolDirectory root) => Path.Combine(root.FullPath, "spool");

    private static HostApplicationBuilder HostWithInheritedHandler(
        TempSpoolDirectory spool,
        StubReceiver receiver,
        TimeSpan deliveryTimeout,
        Action<HttpStandardResilienceOptions>? configureStandardHandler = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options => configureStandardHandler?.Invoke(options)));
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            options.SpoolDirectory = SpoolDirectoryIn(spool);
            options.EndpointBaseUrl = "https://audit.example/api";
            options.ModuleName = "orders-api";
            options.Version = "4.0.0";
            options.DeliveryTimeout = deliveryTimeout;
        });
        builder.Services.AddHttpClient(DrainWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
        return builder;
    }

    private static async Task SpoolOneRecordAsync(IHost host) =>
        await host.Services.GetRequiredService<SpoolWriter>().WriteAsync(SpoolEnvelopes.CreatedAt(DateTimeOffset.UtcNow));

    private static DrainWorker DrainWorkerOf(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<DrainWorker>().Single();

    [Fact]
    public async Task InheritedHandler_ReceiverFailingOnce_ReceivesExactlyOneSendPerDrain()
    {
        using var spool = new TempSpoolDirectory();
        var receiver = FailingOnceThenOk();
        using var host = HostWithInheritedHandler(spool, receiver, TimeSpan.FromSeconds(30)).Build();
        await SpoolOneRecordAsync(host);

        await DrainWorkerOf(host).DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, receiver.Sends);

        await DrainWorkerOf(host).DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, receiver.Sends);
        Assert.Empty(Directory.EnumerateFiles(SpoolDirectoryIn(spool)));
    }

    [Fact]
    public async Task InheritedHandlerAttemptTimeoutShorterThanDeliveryTimeout_SlowReceiver_DeliversOnFirstSend()
    {
        using var spool = new TempSpoolDirectory();
        var receiver = AnsweringOkAfter(TimeSpan.FromMilliseconds(500));
        using var host = HostWithInheritedHandler(
            spool,
            receiver,
            TimeSpan.FromSeconds(2),
            options => options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100)).Build();
        await SpoolOneRecordAsync(host);

        await DrainWorkerOf(host).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, receiver.Sends);
        Assert.Empty(Directory.EnumerateFiles(SpoolDirectoryIn(spool)));
    }

    [Fact]
    public async Task InheritedHandler_DeliveryFails_LogsNothingFromPollyAtError()
    {
        using var spool = new TempSpoolDirectory();
        var receiver = AnsweringOkAfter(TimeSpan.FromSeconds(5));
        var logs = new RecordingLoggerProvider();
        var builder = HostWithInheritedHandler(
            spool,
            receiver,
            TimeSpan.FromMilliseconds(300),
            options => options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100));
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();
        await SpoolOneRecordAsync(host);

        await DrainWorkerOf(host).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(Directory.EnumerateFiles(SpoolDirectoryIn(spool)));
        Assert.DoesNotContain(logs.CategoriesAt(LogLevel.Error), category => category.StartsWith("Polly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsumerHandlerAddedAfterAddEncryptedSpoolAuditSink_StaysInThePipeline()
    {
        using var spool = new TempSpoolDirectory();
        var receiver = FailingOnceThenOk();
        var builder = HostWithInheritedHandler(spool, receiver, TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient(DrainWorker.HttpClientName).AddResilienceHandler(
            "consumer",
            pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.Zero }));
        using var host = builder.Build();
        await SpoolOneRecordAsync(host);

        await DrainWorkerOf(host).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, receiver.Sends);
        Assert.Empty(Directory.EnumerateFiles(SpoolDirectoryIn(spool)));
    }
}
