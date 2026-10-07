using System.Net;
using System.Text.Json;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>One request the drain worker sent, as it arrived at the receiver.</summary>
/// <param name="ContentLength">The declared length, read before the body: <see langword="null"/> means the body was sent chunked.</param>
internal sealed record ReceivedRequest(Uri Uri, byte[] Body, string? ContentType, long? ContentLength);

/// <summary>
/// The audit receiver, faked at the HTTP boundary: it records what was posted and answers as the
/// test lined up. A batch body (a JSON array) is flattened into <see cref="Received"/>, so a test
/// reads the envelopes in the order they arrived whichever endpoint carried them.
/// </summary>
internal sealed class FakeAuditReceiver(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly List<(ReceivedRequest Request, SpoolEnvelope[] Envelopes)> _requests = [];

    /// <summary>Answers every request with <paramref name="statuses"/> in turn, the last one repeating.</summary>
    public static FakeAuditReceiver Responding(params HttpStatusCode[] statuses) =>
        new((requestIndex, _) => Task.FromResult(new HttpResponseMessage(statuses[Math.Min(requestIndex, statuses.Length - 1)])));

    /// <summary>Fails every request with <paramref name="failure"/>, after recording what it was sent.</summary>
    public static FakeAuditReceiver Failing(Exception failure) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(failure));

    /// <summary>Never answers, so a request only ends when the caller gives up on it.</summary>
    public static FakeAuditReceiver Hanging() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    /// <summary>
    /// Answers as though a redirect had already been followed to <paramref name="finalUri"/>:
    /// the response names a request URI other than the one the worker actually posted to,
    /// which is what an auto-following handler would leave behind.
    /// </summary>
    public static FakeAuditReceiver RespondingFromADifferentUri(Uri finalUri, HttpStatusCode status) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri) }));

    /// <summary>Every envelope received, in arrival order, batches flattened.</summary>
    public IReadOnlyList<SpoolEnvelope> Received
    {
        get { lock (_requests) { return [.. _requests.SelectMany(request => request.Envelopes)]; } }
    }

    public IReadOnlyList<Uri> RequestedUris
    {
        get { lock (_requests) { return [.. _requests.Select(request => request.Request.Uri)]; } }
    }

    public IReadOnlyList<ReceivedRequest> Requests
    {
        get { lock (_requests) { return [.. _requests.Select(request => request.Request)]; } }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read before the body: once buffered, any content reports a length, chunked or not.
        var contentLength = request.Content!.Headers.ContentLength;
        var contentType = request.Content.Headers.ContentType?.ToString();
        var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var envelopes = body.Length > 0 && body[0] == (byte)'['
            ? JsonSerializer.Deserialize<SpoolEnvelope[]>(body)!
            : [JsonSerializer.Deserialize<SpoolEnvelope>(body)!];

        int requestIndex;
        lock (_requests)
        {
            _requests.Add((new ReceivedRequest(request.RequestUri!, body, contentType, contentLength), envelopes));
            requestIndex = _requests.Count - 1;
        }

        return await respond(requestIndex, cancellationToken);
    }
}

/// <summary>Hands out the one client the test's receiver is behind, as <c>AddHttpClient</c> does in production.</summary>
internal sealed class SingleClientHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>
/// A drain worker over its own temporary spool, with the sink that fills it, the receiver it
/// posts to, and the health check that reports on it — all wired the way
/// <c>AddEncryptedSpoolAuditSink</c> wires them, the sink and the worker sharing one
/// <see cref="SpoolUsageTracker"/> and one clock.
/// </summary>
internal sealed class SpoolDrainHarness : IDisposable
{
    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TempSpoolDirectory _root = new();
    private readonly HttpClient _client;
    private readonly EncryptedSpoolAuditSink _sink;

    public SpoolDrainHarness(FakeAuditReceiver receiver, Action<EncryptedSpoolOptions>? configure = null)
    {
        SpoolOptions = new EncryptedSpoolOptions
        {
            // A spool below the temporary root, so its dead-letter sibling is thrown away with it.
            SpoolDirectory = Path.Combine(_root.FullPath, "spool"),
            ModuleName = "orders-api",
            Version = "4.0.0",
            EndpointBaseUrl = "https://audit.example/api"
        };
        configure?.Invoke(SpoolOptions);

        Receiver = receiver;
        _client = new HttpClient(receiver);
        Tracker = new SpoolUsageTracker(new SpoolCapacity(SpoolOptions), Time, SpoolOptions.SpoolFullRecheckInterval);
        _sink = new EncryptedSpoolAuditSink(
            Options.Create(SpoolOptions),
            new SpoolWriter(SpoolOptions.SpoolDirectory),
            Tracker,
            Encryptor,
            Time,
            NullLogger<EncryptedSpoolAuditSink>.Instance);
        DeadLetter = new DeadLetterBox(Options.Create(SpoolOptions));
        Reachability = new EndpointReachability();
        Worker = StartedOver();
        HealthCheck = new EncryptedSpoolHealthCheck(Options.Create(SpoolOptions), DeadLetter, Reachability);
    }

    public EncryptedSpoolOptions SpoolOptions { get; }

    public FakeAuditReceiver Receiver { get; }

    public FakeAuditPayloadEncryptor Encryptor { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public RecordingLogger<DrainWorker> Logger { get; } = new();

    public SpoolUsageTracker Tracker { get; }

    public DeadLetterBox DeadLetter { get; }

    public EndpointReachability Reachability { get; }

    public DrainWorker Worker { get; }

    public EncryptedSpoolHealthCheck HealthCheck { get; }

    public Uri AuditEndpoint => new($"{SpoolOptions.EndpointBaseUrl}/audit");

    public Uri BatchEndpoint => new($"{SpoolOptions.EndpointBaseUrl}/audit/batch");

    /// <summary>Another worker over the same spool, endpoint and directories — what a restarted host builds.</summary>
    public DrainWorker StartedOver() =>
        new(Options.Create(SpoolOptions), new SingleClientHttpClientFactory(_client), DeadLetter, Reachability, Tracker, Time, Logger);

    /// <summary>Spools <paramref name="auditEvent"/> through the sink, exactly as an audited operation does.</summary>
    public async Task<AuditEvent> SpoolAsync(AuditEvent auditEvent)
    {
        Assert.Equal(AuditWriteResult.Stored, await _sink.WriteAsync(auditEvent));
        return auditEvent;
    }

    /// <summary>The audit record inside a delivered envelope, decrypted the way the receiver does.</summary>
    public AuditEvent RecordIn(SpoolEnvelope envelope) =>
        JsonSerializer.Deserialize<SpoolPayload>(Encryptor.Decrypt(envelope.Payload), PayloadJsonOptions)!.AuditEvent;

    public IReadOnlyList<string> SpooledFileNames() => FileNamesIn(SpoolOptions.SpoolDirectory);

    public IReadOnlyList<string> DeadLetteredFileNames() => FileNamesIn(DeadLetter.DirectoryPath);

    /// <summary>Puts <paramref name="contents"/> into the spool under <paramref name="fileName"/>, as a fault on the disk would leave it.</summary>
    public string PutInSpool(string fileName, string contents)
    {
        var path = Path.Combine(SpoolOptions.SpoolDirectory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static IReadOnlyList<string> FileNamesIn(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    public void Dispose()
    {
        _sink.Dispose();
        _client.Dispose();
        Receiver.Dispose();
        _root.Dispose();
    }
}
