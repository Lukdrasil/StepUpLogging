using System.Net;
using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// How fast the drain worker empties a spool against a receiver that takes 15 ms to answer, one
/// record per request (<c>BatchSize</c> 1) and 64 per request (issue #69). The receiver's latency
/// is the whole cost of delivery, so records per second scales with the batch size.
/// </summary>
[MemoryDiagnoser]
public class SpoolDrainBenchmarks
{
    private const int RecordCount = 512;
    private static readonly TimeSpan ReceiverLatency = TimeSpan.FromMilliseconds(15);

    private string _spoolDirectory = null!;
    private EncryptedSpoolOptions _options = null!;
    private DrainWorker _worker = null!;
    private HttpClient _client = null!;

    /// <summary>How many records the worker delivers per request.</summary>
    [Params(1, 64)]
    public int BatchSize { get; set; }

    /// <summary>Builds a worker against a receiver that takes 15 ms to answer.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _spoolDirectory = SpoolBenchmarkSupport.FreshSpoolDirectory(nameof(SpoolDrainBenchmarks));
        _options = SpoolBenchmarkSupport.OptionsFor(_spoolDirectory);
        _options.DeliveryBatchSize = BatchSize;

        _client = new HttpClient(new SlowReceiver(ReceiverLatency));
        _worker = new DrainWorker(
            Options.Create(_options),
            new SingleClientFactory(_client),
            new DeadLetterBox(Options.Create(_options)),
            new EndpointReachability(),
            SpoolBenchmarkSupport.TrackerFor(_options),
            TimeProvider.System,
            NullLogger<DrainWorker>.Instance);
    }

    /// <summary>Refills the spool, since each run drains it.</summary>
    [IterationSetup]
    public void FillSpool()
    {
        // Without an fsync per file: filling the spool is setup, not what is measured.
        var writer = new SpoolWriter(_spoolDirectory, (path, contents) => File.WriteAllBytesAsync(path, contents));
        var createdUtc = DateTimeOffset.UtcNow;
        for (var i = 0; i < RecordCount; i++)
        {
            var envelope = new SpoolEnvelope
            {
                EventId = Guid.CreateVersion7(),
                CreatedUtc = createdUtc.AddMilliseconds(i),
                Payload = new byte[512]
            };
            writer.WriteAsync(SpoolWriter.Prepare(envelope)).GetAwaiter().GetResult();
        }
    }

    /// <summary>Disposes the client and removes the spool.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        SpoolBenchmarkSupport.DeleteWithSiblings(_spoolDirectory);
    }

    /// <summary>Drains <see cref="RecordCount"/> records; reported per record, so its inverse is records per second.</summary>
    [Benchmark(OperationsPerInvoke = RecordCount)]
    public Task DrainSpool() => _worker.DrainAsync(CancellationToken.None);

    private sealed class SlowReceiver(TimeSpan latency) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
